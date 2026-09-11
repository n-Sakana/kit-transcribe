Option Explicit
Dim shell, fso, root, entry, powershell, command
Set shell = CreateObject("WScript.Shell")
Set fso = CreateObject("Scripting.FileSystemObject")
root = fso.GetParentFolderName(WScript.ScriptFullName)
entry = fso.BuildPath(root, "src\app\main.ps1")
powershell = shell.ExpandEnvironmentStrings("%SystemRoot%") & "\System32\WindowsPowerShell\v1.0\powershell.exe"
If Not fso.FileExists(entry) Then
    MsgBox "Transcribe entry was not found: " & entry, vbCritical, "Transcribe"
    WScript.Quit 1
End If
command = Quote(powershell) & " -NoLogo -NoProfile -STA -ExecutionPolicy Bypass -File " & Quote(entry)
On Error Resume Next
shell.Run command, 0, False
If Err.Number <> 0 Then
    MsgBox Err.Description, vbCritical, "Transcribe"
    WScript.Quit 1
End If
On Error GoTo 0
Function Quote(value)
    Quote = Chr(34) & value & Chr(34)
End Function
