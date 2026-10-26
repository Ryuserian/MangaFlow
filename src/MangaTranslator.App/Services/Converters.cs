using System.Globalization;
using Avalonia.Data.Converters;
using Avalonia.Media;

namespace MangaTranslator.App.Services;

/// <summary>IsSelected → 사각형 색상/굵기. true: 진한 주황 3px, false: 스카이블루 2px.</summary>
public sealed class SelectedToStrokeBrushConverter : IValueConverter
{
    public static readonly SelectedToStrokeBrushConverter Instance = new();
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        value is true ? new SolidColorBrush(Color.Parse("#FFFF6D00")) : new SolidColorBrush(Color.Parse("#00BFFF"));
    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}

public sealed class SelectedToStrokeThicknessConverter : IValueConverter
{
    public static readonly SelectedToStrokeThicknessConverter Instance = new();
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        value is true ? 3.0 : 2.0;
    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}

public sealed class SelectedToFillConverter : IValueConverter
{
    public static readonly SelectedToFillConverter Instance = new();
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        value is true ? new SolidColorBrush(Color.Parse("#33FF6D00")) : new SolidColorBrush(Color.Parse("#2200BFFF"));
    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}

/// <summary>Include(false=처리 제외) → 박스 흐림. 미체크 말풍선은 0.28로 희미하게 표시.</summary>
public sealed class IncludeToOpacityConverter : IValueConverter
{
    public static readonly IncludeToOpacityConverter Instance = new();
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        value is false ? 0.28 : 1.0;
    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}

/// <summary>int == ConverterParameter → true. 마법사 페이지 IsVisible 토글용.</summary>
public sealed class IntEqualsConverter : IValueConverter
{
    public static readonly IntEqualsConverter Instance = new();
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        value is int i && parameter is string s && int.TryParse(s, out var n) && i == n;
    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}

/// <summary>int <= ConverterParameter → true. 이미 도달한 스텝( 되돌아가기 가능) 강조용.</summary>
public sealed class IntLessOrEqualConverter : IValueConverter
{
    public static readonly IntLessOrEqualConverter Instance = new();
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        value is int i && parameter is string s && int.TryParse(s, out var n) && i <= n;
    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}

/// <summary>CONF_LOW 이상(저신뢰) → 빨강 점선. 안전: confidence<0.55.</summary>
public sealed class LowConfidenceToBrushConverter : IValueConverter
{
    public static readonly LowConfidenceToBrushConverter Instance = new();
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        value is true ? new SolidColorBrush(Color.Parse("#E8FF3B30")) : new SolidColorBrush(Color.Parse("#00BFFF"));
    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}

/// <summary>스텝바 색상: 현재 페이지=주황/완료=녹색/대기=회색. value=WizardPage, parameter=단계번호.</summary>
public sealed class StepBarBrushConverter : IValueConverter
{
    public static readonly StepBarBrushConverter Instance = new();
    // 대기=오프화이트 배경에 보이는 중그레이(#33FFFFFF은 밝은 카드에서 투명→안 보임, 2026-10-25 라이트 전환)
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        value is int page && parameter is string s && int.TryParse(s, out var n)
            ? (page == n ? new SolidColorBrush(Color.Parse("#FFFF6D00"))
               : page > n ? new SolidColorBrush(Color.Parse("#FF2ECC71"))
               : new SolidColorBrush(Color.Parse("#26000000")))
            : new SolidColorBrush(Color.Parse("#26000000"));
    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}

/// <summary>int >= ConverterParameter → true. max도달 페이지 >= n 이면 스텝칩 클릭 허용.</summary>
public sealed class IntGreaterOrEqualConverter : IValueConverter
{
    public static readonly IntGreaterOrEqualConverter Instance = new();
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        value is int i && parameter is string s && int.TryParse(s, out var n) && i >= n;
    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}

/// <summary>confidence(double) < 0.55 → true(저신뢰 경고 표시용).</summary>
public sealed class ConfidenceLowConverter : IValueConverter
{
    public static readonly ConfidenceLowConverter Instance = new();
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        value is double c && c < 0.55;
    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}

/// <summary>confidence(double) < 0.55 → 빨강, 아니면 기본색 (텍스트 라벨용).</summary>
public sealed class ConfidenceLowToBrushConverter : IValueConverter
{
    public static readonly ConfidenceLowToBrushConverter Instance = new();
    // 밝은 테마: 기본=진한 잉크(#E8EDF4 흰近は white card에서 소실), 저신뢰=강한 레드
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        value is double c && c < 0.55
            ? new SolidColorBrush(Color.Parse("#FFD92D20"))
            : new SolidColorBrush(Color.Parse("#FF1D2129"));
    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}

/// <summary>AppStep == Applied → true (결과 미리보기 표시).</summary>
public sealed class EnumStepAppliedConverter : IValueConverter
{
    public static readonly EnumStepAppliedConverter Instance = new();
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        value?.ToString() == "Applied";
    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}

/// <summary>AppStep != Applied → true (아직 실행 전: 실행 버튼 표시).</summary>
public sealed class EnumStepNotAppliedConverter : IValueConverter
{
    public static readonly EnumStepNotAppliedConverter Instance = new();
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        value?.ToString() != "Applied";
    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}
