using System;

namespace MaxFlowWindows.Core;

public static class MicrophoneActivityMeter
{
	private const double NoiseFloorRms = 0.0048;

	private const double NoiseFloorPeak = 0.008;

	public static double FromPcm16(byte[] buffer, int bytesRecorded)
	{
		if (bytesRecorded < 2)
		{
			return 0.0;
		}
		int num = Math.Min(bytesRecorded, buffer.Length);
		int num2 = num / 2;
		if (num2 == 0)
		{
			return 0.0;
		}
		double num3 = 0.0;
		double num4 = 0.0;
		for (int i = 0; i + 1 < num; i += 2)
		{
			double num5 = (double)BitConverter.ToInt16(buffer, i) / 32768.0;
			double val = Math.Abs(num5);
			num4 = Math.Max(num4, val);
			num3 += num5 * num5;
		}
		return FromRmsAndPeak(Math.Sqrt(num3 / (double)num2), num4);
	}

	public static double FromRmsAndPeak(double rms, double peak)
	{
		rms = Math.Clamp(rms, 0.0, 1.0);
		peak = Math.Clamp(peak, 0.0, 1.0);
		if (rms < 0.0048 && peak < 0.008)
		{
			return 0.0;
		}
		double val = Math.Clamp((20.0 * Math.Log10(Math.Max(rms, 1E-06)) + 52.0) / 34.0, 0.0, 1.0);
		double num = Math.Clamp(Math.Sqrt(peak) * 1.18, 0.0, 1.0);
		return Math.Clamp(Math.Max(val, num * 0.78), 0.0, 1.0);
	}
}
