using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using PCTool.Core.AppInstaller;
using PCTool.Core.Framework;
using PCTool.Core.Pdf;

namespace PCTool.Core.Video;

/// <summary>Công cụ cắt/ghép video bằng FFmpeg. FFmpeg cài riêng bằng winget (Gyan.FFmpeg); PCTool chỉ gọi nó với tham số tách riêng, không qua shell.</summary>
public static class VideoInfo
{
    public const string WingetId = "Gyan.FFmpeg";
    public const int MaxMergeFiles = 50;

    public static readonly string[] Extensions = { ".mp4", ".mkv", ".mov", ".avi", ".webm", ".flv", ".wmv", ".m4v", ".ts", ".mpg", ".mpeg" };

    public static AppEntry InstallEntry { get; } = new()
    {
        Id = "ffmpeg", Name = "FFmpeg", Kind = AppKind.Winget, WingetId = WingetId, Group = "Cắt ghép video"
    };

    /// <summary>Tìm ffmpeg.exe: trong PATH, thư mục Links của winget, thư mục gói Gyan.FFmpeg, rồi Program Files\ffmpeg.</summary>
    public static string? FindFfmpeg(Func<string, bool>? exists = null, Func<string, IEnumerable<string>>? subDirs = null,
        string? pathEnv = null, string? localAppData = null, string? programFiles = null)
    {
        exists ??= File.Exists;
        subDirs ??= d => Directory.Exists(d) ? Directory.EnumerateDirectories(d) : Array.Empty<string>();
        pathEnv ??= Environment.GetEnvironmentVariable("PATH") ?? "";
        localAppData ??= Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        programFiles ??= Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);

        foreach (var dir in pathEnv.Split(';', StringSplitOptions.RemoveEmptyEntries))
        {
            var d = dir.Trim().Trim('"');
            if (d.Length == 0 || !Path.IsPathRooted(d)) continue;
            var p = Path.Combine(d, "ffmpeg.exe");
            if (exists(p)) return p;
        }

        if (!string.IsNullOrEmpty(localAppData))
        {
            var winget = Path.Combine(localAppData, "Microsoft", "WinGet");
            var link = Path.Combine(winget, "Links", "ffmpeg.exe");
            if (exists(link)) return link;
            foreach (var pkg in subDirs(Path.Combine(winget, "Packages")).Where(x => Path.GetFileName(x).StartsWith("Gyan.FFmpeg", StringComparison.OrdinalIgnoreCase)))
                foreach (var build in subDirs(pkg))
                {
                    var p = Path.Combine(build, "bin", "ffmpeg.exe");
                    if (exists(p)) return p;
                }
        }

        if (!string.IsNullOrEmpty(programFiles))
        {
            var p = Path.Combine(programFiles, "ffmpeg", "bin", "ffmpeg.exe");
            if (exists(p)) return p;
        }

        return null;
    }

    private static readonly Regex Plain = new(@"^\d{1,6}(\.\d{1,3})?$", RegexOptions.Compiled);
    private static readonly Regex Clock = new(@"^(?:(\d{1,3}):)?(\d{1,2}):(\d{1,2})(\.\d{1,3})?$", RegexOptions.Compiled);

    /// <summary>Nhận "90", "1:30", "01:02:03", "00:01:30.5". Trả lỗi bằng tiếng Việt nếu sai.</summary>
    public static string? TryParseTime(string? text, out double seconds)
    {
        seconds = 0;
        var t = (text ?? "").Trim();
        if (t.Length == 0) return "Chưa nhập thời gian.";
        if (Plain.IsMatch(t)) { seconds = double.Parse(t, CultureInfo.InvariantCulture); return null; }
        var m = Clock.Match(t);
        if (!m.Success) return "Thời gian phải dạng giờ:phút:giây, ví dụ 00:01:30 (hoặc 1:30, hoặc số giây như 90).";
        int h = m.Groups[1].Success ? int.Parse(m.Groups[1].Value) : 0;
        int mi = int.Parse(m.Groups[2].Value), s = int.Parse(m.Groups[3].Value);
        if (mi > 59 || s > 59) return "Phút và giây phải nhỏ hơn 60.";
        seconds = h * 3600 + mi * 60 + s + (m.Groups[4].Success ? double.Parse("0" + m.Groups[4].Value, CultureInfo.InvariantCulture) : 0);
        return null;
    }

    public static string Fmt(double seconds) => seconds.ToString("0.###", CultureInfo.InvariantCulture);

    public static string? CheckVideo(string? path)
    {
        if (string.IsNullOrWhiteSpace(path)) return "Chưa chọn file.";
        string full;
        try { full = Path.GetFullPath(path); } catch { return "Đường dẫn không hợp lệ."; }
        if (full.StartsWith(@"\\", StringComparison.Ordinal)) return "Chưa hỗ trợ đường dẫn mạng; hãy chép file về ổ máy.";
        if (full.Any(char.IsControl)) return "Đường dẫn có ký tự lạ.";
        if (!Extensions.Contains(Path.GetExtension(full).ToLowerInvariant())) return "Chỉ nhận file video: " + string.Join(", ", Extensions);
        if (!File.Exists(full)) return "Không thấy file: " + Path.GetFileName(full);
        return null;
    }

    /// <summary>So sánh tên "tự nhiên": số được so theo giá trị nên "2" đứng trước "10", "Tap 9" trước "Tap 10".</summary>
    public sealed class NaturalComparer : IComparer<string>
    {
        public static NaturalComparer Instance { get; } = new();

        public int Compare(string? x, string? y)
        {
            x ??= ""; y ??= "";
            int i = 0, j = 0;
            while (i < x.Length && j < y.Length)
            {
                if (char.IsDigit(x[i]) && char.IsDigit(y[j]))
                {
                    int si = i, sj = j;
                    while (i < x.Length && char.IsDigit(x[i])) i++;
                    while (j < y.Length && char.IsDigit(y[j])) j++;
                    var a = x[si..i].TrimStart('0'); var b = y[sj..j].TrimStart('0');
                    if (a.Length != b.Length) return a.Length < b.Length ? -1 : 1;
                    int c = string.CompareOrdinal(a, b);
                    if (c != 0) return c < 0 ? -1 : 1;
                }
                else
                {
                    int c = char.ToUpperInvariant(x[i]).CompareTo(char.ToUpperInvariant(y[j]));
                    if (c != 0) return c < 0 ? -1 : 1;
                    i++; j++;
                }
            }

            int rest = (x.Length - i).CompareTo(y.Length - j);
            return rest != 0 ? rest : string.CompareOrdinal(x, y);
        }
    }

    public static List<string> SortByName(IEnumerable<string> files) =>
        files.OrderBy(f => Path.GetFileName(f), NaturalComparer.Instance).ToList();

    public static List<string> SortByDate(IEnumerable<string> files) =>
        files.OrderBy(f => File.GetLastWriteTimeUtc(f)).ThenBy(f => Path.GetFileName(f), NaturalComparer.Instance).ToList();

    /// <summary>Dòng "file '...'" cho bộ ghép concat của FFmpeg: dùng dấu / và thoát dấu nháy đơn.</summary>
    public static string ConcatListText(IEnumerable<string> files)
    {
        var sb = new StringBuilder();
        foreach (var f in files)
            sb.Append("file '").Append(Path.GetFullPath(f).Replace('\\', '/').Replace("'", "'\\''")).Append("'\n");
        return sb.ToString();
    }
}

/// <summary>Cắt một đoạn video thành file MỚI cạnh file gốc (không ghi đè, không đụng file gốc).</summary>
public sealed class VideoCutFix : IFix
{
    private readonly string _ffmpeg;
    private readonly IProcessRunner _runner;
    private readonly string _input;
    private readonly double _start, _duration;
    private readonly bool _lossless;

    public VideoCutFix(string ffmpeg, string input, string startText, string endText, bool lossless, IProcessRunner? runner = null)
    {
        if (VideoInfo.CheckVideo(input) is { } e0) throw new ArgumentException(e0, nameof(input));
        if (VideoInfo.TryParseTime(startText, out _start) is { } e1) throw new ArgumentException("Điểm bắt đầu: " + e1);
        if (VideoInfo.TryParseTime(endText, out var end) is { } e2) throw new ArgumentException("Điểm kết thúc: " + e2);
        if (end <= _start) throw new ArgumentException("Điểm kết thúc phải sau điểm bắt đầu.");
        _duration = end - _start;
        _ffmpeg = ffmpeg;
        _input = Path.GetFullPath(input);
        _lossless = lossless;
        _runner = runner ?? new ProcessRunner();
        var ext = lossless ? Path.GetExtension(_input) : ".mp4";
        OutputPath = PdfOps.UniquePath(Path.GetDirectoryName(_input)!, Path.GetFileNameWithoutExtension(_input) + "-cat", ext);
    }

    public string OutputPath { get; }

    public string Title => "Cắt video";
    public IReadOnlyList<string> Actions => new[]
    {
        $"Cắt đoạn từ {VideoInfo.Fmt(_start)} giây, dài {VideoInfo.Fmt(_duration)} giây, từ file {Path.GetFileName(_input)}",
        _lossless ? "Cắt nhanh, KHÔNG mã hóa lại (không mất chất lượng, nhưng điểm cắt có thể lệch vài giây theo khung hình chính)"
                  : "Mã hóa lại (H.264/AAC, .mp4): cắt đúng điểm nhưng lâu hơn và có thể giảm chất lượng chút ít",
        $"Lưu thành file mới: {OutputPath}. File gốc giữ nguyên; không ghi đè file nào"
    };
    public bool RequiresRestorePoint => false;
    public bool IsUndoable => false;

    public IReadOnlyList<string> BuildArgs()
    {
        var a = new List<string> { "-hide_banner", "-n", "-ss", VideoInfo.Fmt(_start), "-i", _input, "-t", VideoInfo.Fmt(_duration) };
        if (_lossless) a.AddRange(new[] { "-c", "copy", "-avoid_negative_ts", "make_zero" });
        else a.AddRange(new[] { "-c:v", "libx264", "-crf", "20", "-preset", "veryfast", "-c:a", "aac", "-b:a", "160k" });
        a.Add(OutputPath);
        return a;
    }

    public async Task<FixResult> ExecuteAsync(IProgress<string> progress, CancellationToken ct)
    {
        progress.Report("Đang cắt video...");
        var r = await _runner.RunAsync(_ffmpeg, BuildArgs(), 7200, ct);
        return r.ExitCode == 0 ? FixResult.Succeeded("Đã tạo: " + OutputPath) : FixResult.Failed(FfmpegTail.Of(r));
    }

    public Task<bool> VerifyAsync(CancellationToken ct) => Task.FromResult(File.Exists(OutputPath) && new FileInfo(OutputPath).Length > 0);
}

/// <summary>Ghép nhiều video theo thứ tự thành MỘT file mới.</summary>
public sealed class VideoMergeFix : IFix
{
    private readonly string _ffmpeg;
    private readonly IProcessRunner _runner;
    private readonly IReadOnlyList<string> _inputs;
    private readonly bool _lossless;

    public VideoMergeFix(string ffmpeg, IEnumerable<string> inputs, bool lossless, IProcessRunner? runner = null)
    {
        var list = inputs.Select(Path.GetFullPath).ToList();
        if (list.Count < 2) throw new ArgumentException("Cần ít nhất 2 file để ghép.");
        if (list.Count > VideoInfo.MaxMergeFiles) throw new ArgumentException($"Tối đa {VideoInfo.MaxMergeFiles} file mỗi lần ghép.");
        foreach (var f in list) if (VideoInfo.CheckVideo(f) is { } e) throw new ArgumentException(e);
        _inputs = list;
        _ffmpeg = ffmpeg;
        _lossless = lossless;
        _runner = runner ?? new ProcessRunner();
        var ext = lossless ? Path.GetExtension(list[0]) : ".mp4";
        OutputPath = PdfOps.UniquePath(Path.GetDirectoryName(list[0])!, Path.GetFileNameWithoutExtension(list[0]) + "-ghep", ext);
    }

    public string OutputPath { get; }

    public string Title => "Ghép video";
    public IReadOnlyList<string> Actions => new[]
    {
        $"Ghép {_inputs.Count} file theo thứ tự: " + string.Join(" → ", _inputs.Select(Path.GetFileName)),
        _lossless ? "Ghép nhanh, KHÔNG mã hóa lại: các file phải cùng định dạng, độ phân giải và codec, nếu không kết quả có thể lỗi hình/tiếng"
                  : "Mã hóa lại về 1920x1080, 30 hình/giây (H.264/AAC, .mp4): ghép được file khác định dạng, nhưng lâu hơn. Mỗi file phải có cả hình lẫn tiếng",
        $"Lưu thành file mới: {OutputPath}. File gốc giữ nguyên; không ghi đè file nào"
    };
    public bool RequiresRestorePoint => false;
    public bool IsUndoable => false;

    public IReadOnlyList<string> BuildReencodeArgs()
    {
        var a = new List<string> { "-hide_banner", "-n" };
        foreach (var f in _inputs) { a.Add("-i"); a.Add(f); }
        var sb = new StringBuilder();
        for (int i = 0; i < _inputs.Count; i++)
            sb.Append($"[{i}:v]scale=1920:1080:force_original_aspect_ratio=decrease,pad=1920:1080:(ow-iw)/2:(oh-ih)/2,setsar=1,fps=30[v{i}];")
              .Append($"[{i}:a]aresample=48000,aformat=channel_layouts=stereo[a{i}];");
        for (int i = 0; i < _inputs.Count; i++) sb.Append($"[v{i}][a{i}]");
        sb.Append($"concat=n={_inputs.Count}:v=1:a=1[v][a]");
        a.AddRange(new[] { "-filter_complex", sb.ToString(), "-map", "[v]", "-map", "[a]",
            "-c:v", "libx264", "-crf", "20", "-preset", "veryfast", "-c:a", "aac", "-b:a", "160k", OutputPath });
        return a;
    }

    public static IReadOnlyList<string> BuildLosslessArgs(string listFile, string output) =>
        new[] { "-hide_banner", "-n", "-f", "concat", "-safe", "0", "-i", listFile, "-c", "copy", output };

    public async Task<FixResult> ExecuteAsync(IProgress<string> progress, CancellationToken ct)
    {
        progress.Report("Đang ghép video (có thể mất lâu)...");
        ProcessResult r;
        if (_lossless)
        {
            var list = Path.Combine(Path.GetTempPath(), "PCTool-concat-" + Guid.NewGuid().ToString("N") + ".txt");
            await File.WriteAllTextAsync(list, VideoInfo.ConcatListText(_inputs), new UTF8Encoding(false), ct);
            try { r = await _runner.RunAsync(_ffmpeg, BuildLosslessArgs(list, OutputPath), 7200, ct); }
            finally { try { File.Delete(list); } catch (IOException) { } }
        }
        else r = await _runner.RunAsync(_ffmpeg, BuildReencodeArgs(), 7200, ct);

        return r.ExitCode == 0 ? FixResult.Succeeded("Đã tạo: " + OutputPath) : FixResult.Failed(FfmpegTail.Of(r));
    }

    public Task<bool> VerifyAsync(CancellationToken ct) => Task.FromResult(File.Exists(OutputPath) && new FileInfo(OutputPath).Length > 0);
}

internal static class FfmpegTail
{
    public static string Of(ProcessResult r)
    {
        var t = (string.IsNullOrWhiteSpace(r.Error) ? r.Output : r.Error).Trim();
        if (t.Length > 400) t = t[^400..];
        return $"FFmpeg báo lỗi (mã {r.ExitCode}). {t}";
    }
}
