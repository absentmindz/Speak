using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows.Input;

namespace MaxFlowWindows.Core;

public sealed class ShortcutGesture
{
	public bool Ctrl { get; private set; }

	public bool Alt { get; private set; }

	public bool Shift { get; private set; }

	public bool Win { get; private set; }

	public Key? MainKey { get; private set; }

	public static ShortcutGesture Default => new ShortcutGesture
	{
		Ctrl = true,
		Win = true
	};

	public bool HasAnyModifier
	{
		get
		{
			if (!Ctrl && !Alt && !Shift)
			{
				return Win;
			}
			return true;
		}
	}

	public bool HasMainKey => MainKey.HasValue;

	public static ShortcutGesture Parse(string? value)
	{
		if (string.IsNullOrWhiteSpace(value))
		{
			return Default;
		}
		bool flag = false;
		bool flag2 = false;
		bool flag3 = false;
		bool flag4 = false;
		Key? mainKey = null;
		string[] array = value.Split('+', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
		for (int i = 0; i < array.Length; i++)
		{
			string text = array[i].Trim();
			Key result;
			if (text.Equals("Ctrl", StringComparison.OrdinalIgnoreCase) || text.Equals("Control", StringComparison.OrdinalIgnoreCase))
			{
				flag = true;
			}
			else if (text.Equals("Alt", StringComparison.OrdinalIgnoreCase))
			{
				flag2 = true;
			}
			else if (text.Equals("Shift", StringComparison.OrdinalIgnoreCase))
			{
				flag3 = true;
			}
			else if (text.Equals("Win", StringComparison.OrdinalIgnoreCase) || text.Equals("Windows", StringComparison.OrdinalIgnoreCase))
			{
				flag4 = true;
			}
			else if (Enum.TryParse<Key>(text.Replace(" ", ""), ignoreCase: true, out result) && !IsModifierKey(result))
			{
				mainKey = result;
			}
		}
		if (!flag && !flag2 && !flag3 && !flag4 && !mainKey.HasValue)
		{
			return Default;
		}
		return new ShortcutGesture
		{
			Ctrl = flag,
			Alt = flag2,
			Shift = flag3,
			Win = flag4,
			MainKey = mainKey
		};
	}

	public static ShortcutGesture FromCapture(Key key, ModifierKeys modifiers)
	{
		Key key2 = NormalizeKey(key);
		ShortcutGesture shortcutGesture = new ShortcutGesture();
		ShortcutGesture shortcutGesture2 = shortcutGesture;
		bool flag = modifiers.HasFlag(ModifierKeys.Control);
		if (!flag)
		{
			bool flag2 = (uint)(key2 - 118) <= 1u;
			flag = flag2;
		}
		shortcutGesture2.Ctrl = flag;
		ShortcutGesture shortcutGesture3 = shortcutGesture;
		bool flag3 = modifiers.HasFlag(ModifierKeys.Alt);
		if (!flag3)
		{
			bool flag2 = (uint)(key2 - 120) <= 1u;
			flag3 = flag2;
		}
		shortcutGesture3.Alt = flag3;
		ShortcutGesture shortcutGesture4 = shortcutGesture;
		bool flag4 = modifiers.HasFlag(ModifierKeys.Shift);
		if (!flag4)
		{
			bool flag2 = (uint)(key2 - 116) <= 1u;
			flag4 = flag2;
		}
		shortcutGesture4.Shift = flag4;
		ShortcutGesture shortcutGesture5 = shortcutGesture;
		bool flag5 = modifiers.HasFlag(ModifierKeys.Windows);
		if (!flag5)
		{
			bool flag2 = (uint)(key2 - 70) <= 1u;
			flag5 = flag2;
		}
		shortcutGesture5.Win = flag5;
		shortcutGesture.MainKey = (IsModifierKey(key2) ? ((Key?)null) : new Key?(key2));
		return shortcutGesture;
	}

	public string ToStorageString()
	{
		return string.Join("+", Parts());
	}

	public string ToDisplayString()
	{
		return string.Join(" + ", Parts());
	}

	public int MainVirtualKey()
	{
		if (MainKey.HasValue)
		{
			return KeyInterop.VirtualKeyFromKey(MainKey.Value);
		}
		return 0;
	}

	public int NativeModifierFlags()
	{
		int num = 0x4000;
		if (Alt)
		{
			num |= 1;
		}
		if (Ctrl)
		{
			num |= 2;
		}
		if (Shift)
		{
			num |= 4;
		}
		if (Win)
		{
			num |= 8;
		}
		return num;
	}

	public bool IsUsable()
	{
		int num = new bool[4] { Ctrl, Alt, Shift, Win }.Count((bool item) => item);
		if (!MainKey.HasValue)
		{
			return num >= 2;
		}
		return num > 0;
	}

	public bool IsCurrentlyDown(Func<int, bool> isVirtualKeyDown)
	{
		if (Ctrl && !AnyDown(isVirtualKeyDown, 17, 162, 163))
		{
			return false;
		}
		if (Alt && !AnyDown(isVirtualKeyDown, 18, 164, 165))
		{
			return false;
		}
		if (Shift && !AnyDown(isVirtualKeyDown, 16, 160, 161))
		{
			return false;
		}
		if (Win && !AnyDown(isVirtualKeyDown, 91, 92))
		{
			return false;
		}
		if (!MainKey.HasValue)
		{
			return true;
		}
		int num = KeyInterop.VirtualKeyFromKey(MainKey.Value);
		if (num != 0)
		{
			return isVirtualKeyDown(num);
		}
		return false;
	}

	private IEnumerable<string> Parts()
	{
		if (Ctrl)
		{
			yield return "Ctrl";
		}
		if (Alt)
		{
			yield return "Alt";
		}
		if (Shift)
		{
			yield return "Shift";
		}
		if (Win)
		{
			yield return "Win";
		}
		if (MainKey.HasValue)
		{
			yield return FriendlyKeyName(MainKey.Value);
		}
	}

	private static bool AnyDown(Func<int, bool> isVirtualKeyDown, params int[] virtualKeys)
	{
		return virtualKeys.Any(isVirtualKeyDown);
	}

	private static Key NormalizeKey(Key key)
	{
		if (key != Key.System)
		{
			return key;
		}
		return Key.LeftAlt;
	}

	private static bool IsModifierKey(Key key)
	{
		if ((uint)(key - 70) <= 1u || (uint)(key - 116) <= 5u || key == Key.System)
		{
			return true;
		}
		return false;
	}

	private static string FriendlyKeyName(Key key)
	{
		return key switch
		{
			Key.Space => "Space", 
			Key.Return => "Enter", 
			Key.Escape => "Esc", 
			Key.OemPlus => "Plus", 
			Key.OemMinus => "Minus", 
			Key.OemPeriod => "Period", 
			Key.OemComma => "Comma", 
			_ => key.ToString(), 
		};
	}
}
