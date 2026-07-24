namespace MaxFlowWindows.Core;

public sealed class AudioInputDeviceOption
{
	public int DeviceNumber { get; set; }

	public string Name { get; set; } = "";

	public string DisplayName
	{
		get
		{
			if (DeviceNumber >= 0)
			{
				return $"{Name}  ({DeviceNumber})";
			}
			return Name;
		}
	}
}
