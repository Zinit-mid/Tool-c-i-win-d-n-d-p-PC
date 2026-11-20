namespace PCTool.Core.Framework;

/// <summary>Mức độ của một vấn đề. Giao diện luôn hiển thị kèm chữ, không chỉ dựa vào màu.</summary>
public enum Severity
{
    /// <summary>Chỉ để biết, không cần làm gì.</summary>
    Info = 0,

    /// <summary>Nên xem và xử lý.</summary>
    Warning = 1,

    /// <summary>Ảnh hưởng nặng đến việc dùng máy.</summary>
    Critical = 2
}
