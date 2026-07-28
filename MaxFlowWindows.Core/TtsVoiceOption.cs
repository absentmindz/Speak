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
