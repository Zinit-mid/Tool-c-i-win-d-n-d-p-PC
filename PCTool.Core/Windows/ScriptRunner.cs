namespace PCTool.Core.Windows;

/// <summary>Cách chạy một đoạn PowerShell. Mặc định là <see cref="PowerShellRunner"/>, bài test thay bằng bản giả.</summary>
public delegate Task<PowerShellResult> ScriptRunner(string script, CancellationToken ct);
