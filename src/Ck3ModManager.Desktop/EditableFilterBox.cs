using static Ck3ModManager.Core.AppText;
using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Media;

namespace Ck3ModManager.Desktop;

/// <summary>输入时筛选候选；点击箭头时显示全部现有候选，不额外请求网络。</summary>
public sealed class EditableFilterBox : AutoCompleteBox
{
    private bool _showAll;
    private string? _openedText;
    private TextBox? _inputTextBox;

    protected override Type StyleKeyOverride => typeof(AutoCompleteBox);

    public EditableFilterBox()
    {
        Classes.Add("workshopFilter");
        MinimumPrefixLength = 0;
        MaxDropDownHeight = 280;
        IsTextCompletionEnabled = false;
        FilterMode = AutoCompleteFilterMode.Contains;
        var arrow = new Button
        {
            Content = new PathIcon
            {
                Data = Geometry.Parse("M 0,0 L 4,4 L 8,0 L 9,1 L 4,6 L -1,1 Z"),
                Width = 10, Height = 6
            },
            Width = 28, Height = 26, MinHeight = 0, MinWidth = 0, Padding = new Thickness(0),
            Margin = new Thickness(5, 0, -4, 0), BorderThickness = new Thickness(0),
            Background = Brushes.Transparent, Focusable = false
        };
        arrow.Classes.Add("filterArrow");
        AutomationProperties.SetName(arrow, T("ShowOptions"));
        arrow.Click += (_, _) => ToggleOptions();
        InnerRightContent = arrow;
        DropDownClosed += (_, _) => { _showAll = false; FilterMode = AutoCompleteFilterMode.Contains; };
    }

    public void ToggleOptions()
    {
        if (IsDropDownOpen && _showAll) { SetCurrentValue(IsDropDownOpenProperty, false); return; }
        if (IsDropDownOpen) SetCurrentValue(IsDropDownOpenProperty, false);
        Focus();
        // 箭头用于浏览候选，清除 AutoCompleteBox 获得焦点时的自动全选。
        // 直接点击文字或通过 Tab 进入时，仍保留原生编辑与全选行为。
        if (_inputTextBox is { } input)
        {
            input.SelectionStart = input.SelectionEnd = input.CaretIndex;
        }
        _showAll = true;
        _openedText = Text;
        FilterMode = AutoCompleteFilterMode.None;
        SetCurrentValue(IsDropDownOpenProperty, true);
    }

    protected override void OnApplyTemplate(TemplateAppliedEventArgs e)
    {
        base.OnApplyTemplate(e);
        _inputTextBox = e.NameScope.Find<TextBox>("PART_TextBox");
    }

    protected override void OnTextChanged(TextChangedEventArgs e)
    {
        if (_showAll && Text != _openedText)
        {
            _showAll = false;
            FilterMode = AutoCompleteFilterMode.Contains;
        }
        base.OnTextChanged(e);
    }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        if (e.Key == Key.Down && !IsDropDownOpen)
        {
            ToggleOptions();
            e.Handled = true;
            return;
        }
        base.OnKeyDown(e);
    }
}
