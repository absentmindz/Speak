Set sh = CreateObject("WScript.Shell")
Set shortcut = sh.CreateShortcut("C:\Users\hamza\Desktop\Speak.lnk")
WScript.Echo shortcut.TargetPath
