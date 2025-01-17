/////////////////////////////////////////////////////////////////////////////////
// Paint.NET                                                                   //
// Copyright (C) dotPDN LLC, Rick Brewster, Tom Jackson, and contributors.     //
// Portions Copyright (C) Microsoft Corporation. All Rights Reserved.          //
// See license-pdn.txt for full licensing and attribution details.             //
//                                                                             //
// Ported to Pinta by: Olivier Dufour <olivier.duff@gmail.com>                 //
/////////////////////////////////////////////////////////////////////////////////

using System;
using System.Drawing;
using Cairo;
using Pinta.Core;
using Pinta.Gui.Widgets;

namespace Pinta.Effects;

public enum WarpEdgeBehavior
{
	[Caption ("Clamp")]
	Clamp,

	[Caption ("Wrap")]
	Wrap,

	[Caption ("Reflect")]
	Reflect,

	[Caption ("Primary")]
	Primary,

	[Caption ("Secondary")]
	Secondary,

	[Caption ("Transparent")]
	Transparent,

	[Caption ("Original")]
	Original,
}

public abstract class WarpEffect : BaseEffect
{
	public WarpData Data => (WarpData) EffectData!;

	public WarpEffect ()
	{
		EffectData = new WarpData ();
	}

	public override void LaunchConfiguration ()
	{
		EffectHelper.LaunchSimpleEffectDialog (this);
	}

	protected double DefaultRadius { get; private set; } = 0;
	protected double DefaultRadius2 { get; private set; } = 0;

	#region Algorithm Code Ported From PDN
	public override void Render (ImageSurface src, ImageSurface dst, ReadOnlySpan<RectangleI> rois)
	{
		WarpSettings settings = CreateSettings ();

		DefaultRadius = Math.Min (settings.selection.Width, settings.selection.Height) * 0.5;
		DefaultRadius2 = DefaultRadius * DefaultRadius;

		Span<PointD> aaPoints = stackalloc PointD[settings.aaSampleCount];
		Utility.GetRgssOffsets (aaPoints, settings.aaSampleCount, Data.Quality);

		Span<ColorBgra> dst_data = dst.GetPixelData ();
		ReadOnlySpan<ColorBgra> src_data = src.GetReadOnlyPixelData ();

		foreach (RectangleI rect in rois) {
			for (int y = rect.Top; y <= rect.Bottom; y++) {
				var dst_row = dst_data.Slice (y * dst.Width, dst.Width);
				double relativeY = y - settings.y_center_offset;
				for (int x = rect.Left; x <= rect.Right; x++) {
					PointI target = new (x, y);
					dst_row[x] = GetPixelColor (settings, src, src_data, aaPoints, relativeY, target);
				}
			}
		}
	}

	private sealed record WarpSettings (
		RectangleI selection,
		double x_center_offset,
		double y_center_offset,
		ColorBgra colPrimary,
		ColorBgra colSecondary,
		ColorBgra colTransparent,
		int aaSampleCount,
		WarpEdgeBehavior edgeBehavior);
	private WarpSettings CreateSettings ()
	{
		var selection = PintaCore.LivePreview.RenderBounds;
		return new (
			selection: selection,
			x_center_offset: selection.Left + (selection.Width * (1.0 + Data.CenterOffset.X) * 0.5),
			y_center_offset: selection.Top + (selection.Height * (1.0 + Data.CenterOffset.Y) * 0.5),
			colPrimary: PintaCore.Palette.PrimaryColor.ToColorBgra (),
			colSecondary: PintaCore.Palette.SecondaryColor.ToColorBgra (),
			colTransparent: ColorBgra.Transparent,
			aaSampleCount: Data.Quality * Data.Quality,
			edgeBehavior: Data.EdgeBehavior
		);
	}

	private ColorBgra GetPixelColor (
		WarpSettings settings,
		ImageSurface src,
		ReadOnlySpan<ColorBgra> src_data,
		ReadOnlySpan<PointD> aaPoints,
		double relativeY,
		PointI target)
	{
		Span<ColorBgra> samples = stackalloc ColorBgra[settings.aaSampleCount];
		double relativeX = target.X - settings.x_center_offset;
		int sampleCount = 0;
		for (int p = 0; p < settings.aaSampleCount; ++p) {
			TransformData initialTd = new (
				X: relativeX + aaPoints[p].X,
				Y: relativeY - aaPoints[p].Y
			);
			TransformData td = InverseTransform (initialTd);
			PointF preliminarySample = new (
				x: (float) (td.X + settings.x_center_offset),
				y: (float) (td.Y + settings.y_center_offset)
			);
			samples[sampleCount] = GetSample (settings, src, src_data, target, preliminarySample);
			++sampleCount;
		}
		return ColorBgra.Blend (samples[..sampleCount]);
	}

	private static ColorBgra GetSample (WarpSettings settings, ImageSurface src, ReadOnlySpan<ColorBgra> src_data, PointI target, PointF preliminarySample)
	{
		if (IsOnSurface (src, preliminarySample.X, preliminarySample.Y))
			return src.GetBilinearSample (preliminarySample.X, preliminarySample.Y);

		return settings.edgeBehavior switch {
			WarpEdgeBehavior.Clamp => src.GetBilinearSampleClamped (preliminarySample.X, preliminarySample.Y),
			WarpEdgeBehavior.Wrap => src.GetBilinearSampleWrapped (preliminarySample.X, preliminarySample.Y),
			WarpEdgeBehavior.Reflect => src.GetBilinearSampleClamped (ReflectCoord (preliminarySample.X, src.Width), ReflectCoord (preliminarySample.Y, src.Height)),
			WarpEdgeBehavior.Primary => settings.colPrimary,
			WarpEdgeBehavior.Secondary => settings.colSecondary,
			WarpEdgeBehavior.Transparent => settings.colTransparent,
			WarpEdgeBehavior.Original => src_data[target.Y * src.Width + target.X],
			_ => settings.colPrimary,
		};
	}

	protected abstract TransformData InverseTransform (TransformData data);

	protected readonly record struct TransformData (double X, double Y);

	private static bool IsOnSurface (ImageSurface src, float u, float v)
	{
		return (u >= 0 && u <= (src.Width - 1) && v >= 0 && v <= (src.Height - 1));
	}

	private static float ReflectCoord (float value, int max)
	{
		bool reflection = false;

		while (value < 0) {
			value += max;
			reflection = !reflection;
		}

		while (value > max) {
			value -= max;
			reflection = !reflection;
		}

		if (reflection) {
			value = max - value;
		}

		return value;
	}

	#endregion
	public class WarpData : EffectData
	{
		[Caption ("Quality"), MinimumValue (1), MaximumValue (5)]
		public int Quality { get; set; } = 2;

		[Caption ("Center Offset")]
		public PointD CenterOffset { get; set; }

		public WarpEdgeBehavior EdgeBehavior { get; set; } = WarpEdgeBehavior.Wrap;
	}
}
