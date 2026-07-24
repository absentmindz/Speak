using System;
using System.IO;
using System.Media;
using System.Threading.Tasks;

namespace MaxFlowWindows.Core;

public static class RecordingFeedbackSound
{
	private const int SampleRate = 44100;

	private const short Channels = 1;

	private const short BitsPerSample = 16;

	private const double Volume = 0.055;

	private static readonly byte[] StartSound = BuildTone(1120, 261.63, 329.63, 360, 680);

	private static readonly byte[] StopSound = BuildTone(960, 329.63, 246.94, 300, 620);

	public static void PlayStart()
	{
		Play(StartSound);
	}

	public static void PlayStop()
	{
		Play(StopSound);
	}

	private static void Play(byte[] wavBytes)
	{
		Task.Run(delegate
		{
			try
			{
				using MemoryStream stream = new MemoryStream(wavBytes, writable: false);
				using SoundPlayer soundPlayer = new SoundPlayer(stream);
				soundPlayer.PlaySync();
			}
			catch (Exception exception)
			{
				AppLog.Warn("Recording feedback sound failed.", exception);
			}
		});
	}

	private static byte[] BuildTone(int durationMs, double firstFrequency, double secondFrequency, int secondDelayMs, int releaseMs)
	{
		int num = 44100 * durationMs / 1000;
		byte[] array = new byte[num * 2];
		int num2 = 5292;
		int num3 = Math.Max(1, 44100 * releaseMs / 1000);
		int num4 = 44100 * secondDelayMs / 1000;
		int num5 = 9702;
		for (int i = 0; i < num; i++)
		{
			double num6 = (double)i / 44100.0;
			double num7 = ((i < num2) ? ((double)i / (double)num2) : 1.0);
			double num8 = ((i > num - num3) ? ((double)(num - i) / (double)num3) : 1.0);
			double num9 = Math.Clamp(num7 * num8, 0.0, 1.0);
			double num10 = Math.Sin(Math.PI * 2.0 * firstFrequency * num6);
			int num11 = i - num4;
			double num12 = ((((num11 > 0) ? Math.Clamp((double)num11 / (double)num5, 0.0, 1.0) : 0.0) > 0.0) ? Math.Sin(Math.PI * 2.0 * secondFrequency * (num6 - (double)num4 / 44100.0)) : 0.0);
			short num13 = (short)Math.Clamp((num10 * 0.56 + num12 * 0.3) * num9 * 0.055 * 32767.0, -32768.0, 32767.0);
			int num14 = i * 2;
			array[num14] = (byte)(num13 & 0xFF);
			array[num14 + 1] = (byte)((num13 >> 8) & 0xFF);
		}
		return BuildWaveFile(array);
	}

	private static byte[] BuildWaveFile(byte[] pcm)
	{
		int value = 88200;
		short value2 = 2;
		using MemoryStream memoryStream = new MemoryStream();
		using BinaryWriter binaryWriter = new BinaryWriter(memoryStream);
		WriteAscii(binaryWriter, "RIFF");
		binaryWriter.Write(36 + pcm.Length);
		WriteAscii(binaryWriter, "WAVE");
		WriteAscii(binaryWriter, "fmt ");
		binaryWriter.Write(16);
		binaryWriter.Write((short)1);
		binaryWriter.Write((short)1);
		binaryWriter.Write(44100);
		binaryWriter.Write(value);
		binaryWriter.Write(value2);
		binaryWriter.Write((short)16);
		WriteAscii(binaryWriter, "data");
		binaryWriter.Write(pcm.Length);
		binaryWriter.Write(pcm);
		binaryWriter.Flush();
		return memoryStream.ToArray();
	}

	private static void WriteAscii(BinaryWriter writer, string value)
	{
		foreach (char c in value)
		{
			writer.Write((byte)c);
		}
	}
}
