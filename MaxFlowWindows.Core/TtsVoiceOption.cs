using System;
using System.Collections.Generic;
using System.Linq;

namespace MaxFlowWindows.Core;

public sealed class TtsVoiceOption
{
	private static readonly string[] CustomVoiceNames =
	{
		"Aiden",
		"Dylan",
		"Eric",
		"Ono_anna",
		"Ryan",
		"Serena",
		"Sohee",
		"Uncle_fu",
		"Vivian"
	};

	private static readonly (string Id, string Name)[] EdgeVoiceNames =
	{
		("en-US-BrianNeural", "Brian — warm male"),
		("en-US-AndrewNeural", "Andrew — narration male"),
		("en-US-GuyNeural", "Guy — clear male"),
		("en-US-RogerNeural", "Roger — mature male"),
		("en-US-AvaNeural", "Ava — polished female"),
		("en-US-AriaNeural", "Aria — general female"),
		("en-US-JennyNeural", "Jenny — friendly female")
	};

	public string Id { get; set; } = "";

	public string Name { get; set; } = "";

	public string EngineId { get; set; } = "";

	public static IReadOnlyList<TtsVoiceOption> ForEngine(string engineId)
	{
		if (engineId.Equals("qwen3-customvoice-1.7b", StringComparison.OrdinalIgnoreCase))
		{
			return CustomVoiceNames.Select((string voice) => new TtsVoiceOption
			{
				Id = voice,
				Name = voice.Replace("_", " "),
				EngineId = engineId
			}).ToList();
		}
		if (engineId.Equals("edge-tts", StringComparison.OrdinalIgnoreCase))
		{
			return EdgeVoiceNames.Select((voice) => new TtsVoiceOption
			{
				Id = voice.Id,
				Name = voice.Name,
				EngineId = engineId
			}).ToList();
		}
		if (engineId.Equals("tortoise-ultra-fast", StringComparison.OrdinalIgnoreCase))
		{
			return new List<TtsVoiceOption>
			{
				new TtsVoiceOption
				{
					Id = "daniel",
					Name = "Daniel",
					EngineId = engineId
				}
			};
		}
		return new List<TtsVoiceOption>
		{
			new TtsVoiceOption
			{
				Id = "default",
				Name = "Default voice",
				EngineId = engineId
			}
		};
	}
}
