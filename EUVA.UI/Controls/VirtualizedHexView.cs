// SPDX-License-Identifier: GPL-3.0-or-later


using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using EUVA.Core.Models;
using EUVA.UI;
using System.IO.MemoryMappedFiles;



namespace EUVA.UI.Controls;




public class VirtualizedHexView : FrameworkElement
{   

    private char[] _asciiLookupTable = new char[256];
    private int _currentCodePage = 1251;    


public void InitializeAsciiTable(int codePage)
{
    _currentCodePage = codePage;
    try 
    {
        System.Text.Encoding.RegisterProvider(System.Text.CodePagesEncodingProvider.Instance);
        var encoding = System.Text.Encoding.GetEncoding(codePage);
        
        
        byte[] allBytes = new byte[256];
        for (int i = 0; i < 256; i++) allBytes[i] = (byte)i;
        string decoded = encoding.GetString(allBytes);

        for (int i = 0; i < 256; i++)
        {
            if (i < 32 || i == 127) 
            {
                _asciiLookupTable[i] = '.'; 
            }
            else if (i < 127) 
            {
                
                _asciiLookupTable[i] = (char)i; 
            }
            else 
            {
                
                char c = decoded[i];
                _asciiLookupTable[i] = char.IsControl(c) ? '.' : c;
            }
        }
    }
    catch 
    {
        for (int i = 0; i < 256; i++)
            _asciiLookupTable[i] = (i >= 32 && i <= 126) ? (char)i : '.';
    }
}

    public void ChangeEncoding(int codePage)
    {
        InitializeAsciiTable(codePage);
        InvalidateVisual(); 
    }

    
    private readonly HashSet<long> _modifiedOffsets = new();
    private MemoryMappedFile? _mmf;
    private MemoryMappedViewAccessor? _accessor;
    private long _fileLength;
    
    public long FileLength => _fileLength;
    private long _currentScrollLine = 0; 
    private long _selectionStart = -1;
    private long _selectionEnd = -1;
    private bool HasSelection => _selectionStart != -1 && _selectionEnd != -1;
    private long SelectionMin => Math.Min(_selectionStart, _selectionEnd);
    private long SelectionMax => Math.Max(_selectionStart, _selectionEnd);
    public static bool IsMadnessMode { get; set; } = false;
    private readonly ScrollViewer _scrollViewer;
    private byte[] _data = Array.Empty<byte>();
    private List<DataRegion> _regions = new();
    private long _selectedOffset = -1;
    
    private double _lineHeight = 18;
    private double _charWidth = 9;
    
    
    private readonly Typeface _typeface = new(new FontFamily("Consolas"), 
        FontStyles.Normal, FontWeights.Normal, FontStretches.Normal);
    private readonly double _fontSize = 13;

    
    
    

    public static readonly DependencyProperty RegionsProperty =
        DependencyProperty.Register(nameof(Regions), typeof(List<DataRegion>), typeof(VirtualizedHexView),
            new PropertyMetadata(new List<DataRegion>(), OnRegionsChanged));

    public static readonly DependencyProperty SelectedOffsetProperty =
        DependencyProperty.Register(nameof(SelectedOffset), typeof(long), typeof(VirtualizedHexView),
            new PropertyMetadata(-1L, OnSelectedOffsetChanged));

    public byte ReadByte(long offset)
    {
        if (_accessor == null || offset < 0 || offset >= _fileLength)
            return 0;
        
        return _accessor.ReadByte(offset);
    }


    public void LoadFile(string filePath)
    {
        _accessor?.Dispose();
        _mmf?.Dispose();

        if (!File.Exists(filePath)) return;

        var fi = new FileInfo(filePath);
        _fileLength = fi.Length;

        
        _mmf = MemoryMappedFile.CreateFromFile(filePath, FileMode.Open, null, 0, MemoryMappedFileAccess.ReadWrite);
        _accessor = _mmf.CreateViewAccessor(0, 0, MemoryMappedFileAccess.ReadWrite);

        _currentScrollLine = 0;
        InvalidateVisual();
    }

    
        public void WriteByte(long offset, byte value)
    {
        if (_accessor == null) return;
        
        _accessor.Write(offset, value);
        
        
        lock (_modifiedOffsets) 
        {
            _modifiedOffsets.Add(offset);
        }
    }

    
    
    
    
    

    

    
    

    
    
    

    
    
    
    
    

    
    

    public void ReadBytes(long offset, byte[] buffer)
    {
        if (_accessor == null || offset < 0 || offset >= _fileLength)
            return;

        
        int count = (int)Math.Min(buffer.Length, _fileLength - offset);
        if (count <= 0) return;

        _accessor.ReadArray(offset, buffer, 0, count);
    }

    public void Dispose()
    {
        _accessor?.Dispose();
        _mmf?.Dispose();
    }


    
    
    
    
    

    public List<DataRegion> Regions
    {
        get => (List<DataRegion>)GetValue(RegionsProperty);
        set => SetValue(RegionsProperty, value);
    }

    public long SelectedOffset
    {
        get => (long)GetValue(SelectedOffsetProperty);
        set => SetValue(SelectedOffsetProperty, value);
    }

    public event EventHandler<long>? OffsetSelected;

    
    
    private static Brush ThemeBrush(string key, Brush fallback)
    {
        var res = Application.Current?.TryFindResource(key);
        return res is Brush b ? b : fallback;
    }
  
    private static Brush BrushBackground     => ThemeBrush("Hex_Background",        new SolidColorBrush(Color.FromRgb( 30,  30,  30)));
    private static Brush BrushOffset         => ThemeBrush("HexOffset",              new SolidColorBrush(Color.FromRgb(160, 160, 160)));
    private static Brush BrushByteActive     => ThemeBrush("Hex_ByteActive",         new SolidColorBrush(Color.FromRgb(173, 216, 230)));
    private static Brush BrushByteNull       => ThemeBrush("Hex_ByteNull",           new SolidColorBrush(Color.FromRgb( 80,  80,  80)));
    private static Brush BrushByteSelected   => ThemeBrush("Hex_ByteSelected",       new SolidColorBrush(Color.FromRgb(255, 255,   0)));
    private static Brush BrushAsciiPrintable => ThemeBrush("Hex_AsciiPrintable",     new SolidColorBrush(Color.FromRgb(144, 238, 144)));
    private static Brush BrushAsciiNonPrint  => ThemeBrush("Hex_AsciiNonPrintable",  new SolidColorBrush(Color.FromRgb(100, 100, 100)));
    private static Brush BrushColumnHeader   => ThemeBrush("ForegroundSecondary",    new SolidColorBrush(Color.FromRgb(100, 100, 100)));

    public VirtualizedHexView()
    {
        _scrollViewer = new ScrollViewer
        {
            VerticalScrollBarVisibility = ScrollBarVisibility.Visible,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Auto,
            Background = new SolidColorBrush(Color.FromRgb(30, 30, 30))
        };

        InitializeAsciiTable(28591);

        ClipToBounds = true;
        Focusable = true;
        
        MouseDown += OnMouseDown;
        MouseWheel += OnMouseWheel;
    }

    
    
    
    
    
    
    
    

    private static void OnRegionsChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is VirtualizedHexView hexView)
        {
            hexView._regions = (List<DataRegion>)e.NewValue;
            hexView.InvalidateVisual();
        }
    }

    private static void OnSelectedOffsetChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is VirtualizedHexView hexView)
        {
            hexView._selectedOffset = (long)e.NewValue;
            hexView.ScrollToOffset(hexView._selectedOffset);
            hexView.InvalidateVisual();
        }
    }

    
    
    
    public void ScrollToOffset(long offset)
    {
        if (offset < 0 || offset >= _fileLength)
            return;

        
        _currentScrollLine = offset / _bytesPerLine;
        
        InvalidateVisual();
    }

    
    
    


    private int _bytesPerLine = 24; 

    
    protected override void OnRender(DrawingContext dc)
    {
        base.OnRender(dc);

        if (_fileLength == 0 || _accessor == null)
        {
            DrawEmptyState(dc);
            return;
        }

        dc.DrawRectangle(BrushBackground, null, new Rect(0, 0, ActualWidth, ActualHeight));

        long totalLines = (_fileLength + _bytesPerLine - 1) / _bytesPerLine;
        int visibleLines = (int)(ActualHeight / _lineHeight) + 2;
        
        long firstVisibleLine = _currentScrollLine; 
        long lastVisibleLine = Math.Min(firstVisibleLine + visibleLines, totalLines);

        
        double offsetColumnWidth = 120;
        
        double hexColumnWidth = _bytesPerLine * 3 * _charWidth; 
        
        double asciiColumnStart = offsetColumnWidth + hexColumnWidth + 20;

        DrawColumnHeaders(dc, offsetColumnWidth, asciiColumnStart);

        for (long line = firstVisibleLine; line < lastVisibleLine; line++)
        {
            long offset = line * _bytesPerLine;
            double y = (line - firstVisibleLine) * _lineHeight + 25; 

            if (offset < _fileLength)
            {
                DrawLine(dc, offset, y, offsetColumnWidth, asciiColumnStart);
            }
        }
    }

    public void JumpToNextChange()
    {
        
        if (_modifiedOffsets.Count == 0) return;

        lock (_modifiedOffsets)
        {
            
            
            long startSearchFrom = _selectedOffset;

            
            var nextChange = _modifiedOffsets
                .Where(o => o > startSearchFrom)
                .OrderBy(o => o)
                .Cast<long?>()
                .FirstOrDefault();

            
            
            if (nextChange == null)
            {
                nextChange = _modifiedOffsets.Min();
            }

            if (nextChange.HasValue)
            {
                
                _selectedOffset = nextChange.Value;

                
                
                long targetLine = _selectedOffset / _bytesPerLine;
                _currentScrollLine = Math.Max(0, targetLine - 2); 

                
                OffsetSelected?.Invoke(this, _selectedOffset);
                InvalidateVisual();
            }
        }
    }

    private void DrawEmptyState(DrawingContext dc)
    {
        dc.DrawRectangle(BrushBackground, null, new Rect(0, 0, ActualWidth, ActualHeight));

        var text = new FormattedText("No file loaded. Drag & drop PE file here.",
            System.Globalization.CultureInfo.CurrentCulture,
            FlowDirection.LeftToRight, _typeface, _fontSize,
            BrushColumnHeader, VisualTreeHelper.GetDpi(this).PixelsPerDip);

        dc.DrawText(text, new Point((ActualWidth - text.Width) / 2,
            (ActualHeight - text.Height) / 2));
    }

    private void DrawColumnHeaders(DrawingContext dc, double offsetColumnWidth, double asciiColumnStart)
    {
        var brush = BrushColumnHeader;

        var offsetHeader = CreateFormattedText("Offset", brush);
        dc.DrawText(offsetHeader, new Point(10, 5));

        var hexHeader = CreateFormattedText("Hex View", brush);
        dc.DrawText(hexHeader, new Point(offsetColumnWidth + 10, 5));

        var asciiHeader = CreateFormattedText("ASCII", brush);
        dc.DrawText(asciiHeader, new Point(asciiColumnStart, 5));
    }



    public bool IsMediaMode { get; set; } = false;
    private byte[] _mediaBuffer;
    private readonly string _videoRamp = " .:-=+*#%@"; 

    public void SetMediaFrame(byte[] frame)
    {
        _mediaBuffer = frame;
        this.InvalidateVisual();
    }


private void DrawLine(DrawingContext dc, long offset, double y, 
    double offsetColumnWidth, double asciiColumnStart)
{
    if (_accessor == null || offset >= _fileLength) return;

    
    var addressText = CreateFormattedText($"{offset:X8}", BrushOffset);
    dc.DrawText(addressText, new Point(10, y));

    
    int bytesToDraw = (int)Math.Min(_bytesPerLine, _fileLength - offset);
    byte[] lineBuffer = new byte[bytesToDraw];

    if (IsMediaMode && _mediaBuffer != null)
    {
        int startIdx = (int)(offset - (long)(_currentScrollLine * _bytesPerLine));
        for (int i = 0; i < bytesToDraw; i++)
        {
            int bufferIdx = startIdx + i;
            lineBuffer[i] = (bufferIdx >= 0 && bufferIdx < _mediaBuffer.Length) ? _mediaBuffer[bufferIdx] : (byte)0;
        }
    }
    else
    {
        
        _accessor.ReadArray(offset, lineBuffer, 0, bytesToDraw);
    }

    
    for (int i = 0; i < bytesToDraw; i++)
    {
        long byteOffset = offset + i;
        byte value = lineBuffer[i];
        double x = offsetColumnWidth + 10 + i * 3 * _charWidth;

        var region = _regions.FirstOrDefault(r => r.Contains(byteOffset));
        if (region != null)
        {
            var regBrush = new SolidColorBrush(Color.FromArgb(60, region.HighlightColor.R, region.HighlightColor.G, region.HighlightColor.B));
            dc.DrawRectangle(regBrush, null, new Rect(x - 2, y - 2, _charWidth * 2.5, _lineHeight));
        }

        if (HasSelection && byteOffset >= SelectionMin && byteOffset <= SelectionMax)
        {
            dc.DrawRectangle(new SolidColorBrush(Color.FromArgb(100, 51, 153, 255)), null, new Rect(x - 2, y - 2, _charWidth * 2.5, _lineHeight));
        }

        if (_modifiedOffsets.Contains(byteOffset))
        {
        
            var modBackground = new SolidColorBrush(Color.FromArgb(80, 255, 0, 128)); 
            dc.DrawRectangle(modBackground, null, new Rect(x - 2, y - 2, _charWidth * 2.5, _lineHeight));
        }

        Brush hexBrush = (byteOffset == _selectedOffset) ? BrushByteSelected : 
                         (value == 0x00) ? BrushByteNull : BrushByteActive;
        
        var hexText = CreateFormattedText($"{value:X2}", hexBrush);
        dc.DrawText(hexText, new Point(x, y));

        if (byteOffset == _selectedOffset)
        {
            dc.DrawRectangle(null, new Pen(BrushByteSelected, 1), new Rect(x - 2, y - 2, _charWidth * 2.5, _lineHeight));
        }
    }

    
    for (int i = 0; i < bytesToDraw; i++)
    {
        long byteOffset = offset + i;
        byte value = lineBuffer[i]; 
        double xAscii = asciiColumnStart + i * _charWidth;

        if (HasSelection && byteOffset >= SelectionMin && byteOffset <= SelectionMax)
        {
            dc.DrawRectangle(new SolidColorBrush(Color.FromArgb(100, 51, 153, 255)), null, new Rect(xAscii, y - 2, _charWidth, _lineHeight));
        }

        char displayChar;
        Brush asciiBrush;

        if (IsMediaMode)
        {
            
            int rampIndex = value * (_videoRamp.Length - 1) / 255;
            displayChar = _videoRamp[rampIndex];
            asciiBrush = BrushAsciiPrintable; 
        }
        else
        {
            
            displayChar = _asciiLookupTable[value];
            if (value >= 32 && value <= 126) 
                asciiBrush = BrushAsciiPrintable;
            else if (value > 127 && displayChar != '.') 
                asciiBrush = new SolidColorBrush(Color.FromRgb(60, 120, 60));
            else 
                asciiBrush = BrushAsciiNonPrint;
        }

        var asciiText = CreateFormattedText(displayChar.ToString(), asciiBrush);
        dc.DrawText(asciiText, new Point(xAscii, y));
    }
}

public void Save()
{
    _accessor.Flush(); 
}

  private FormattedText CreateFormattedText(string text, Brush brush)
    {
        return new FormattedText(
            text,
            System.Globalization.CultureInfo.InvariantCulture, 
            FlowDirection.LeftToRight,
            _typeface,
            _fontSize,
            brush,
            VisualTreeHelper.GetDpi(this).PixelsPerDip);
    }

    private void OnMouseDown(object sender, MouseButtonEventArgs e)
    {
        Focus();
        
        var position = e.GetPosition(this);
        long offset = CalculateOffsetFromPosition(position);

        if (offset >= 0 && offset < _data.Length)
        {
            SelectedOffset = offset;
            OffsetSelected?.Invoke(this, offset);
        }
    }

    private void OnMouseWheel(object sender, MouseWheelEventArgs e)
    {
        e.Handled = true;

    
    int multiplier = 1;
    if (Keyboard.Modifiers.HasFlag(ModifierKeys.Control)) multiplier = 100;
    else if (Keyboard.Modifiers.HasFlag(ModifierKeys.Shift)) multiplier = 1000;

    int linesToScroll = (e.Delta > 0 ? -3 : 3) * multiplier;
    
    long maxLines = _fileLength / _bytesPerLine;
    _currentScrollLine = Math.Clamp(_currentScrollLine + linesToScroll, 0, maxLines);

    InvalidateVisual();
    }
    

    private long CalculateOffsetFromPosition(Point position)
{
    double offsetColumnWidth = 120;
    double hexColumnWidth = _bytesPerLine * 3 * _charWidth;
    double asciiColumnStart = offsetColumnWidth + hexColumnWidth + 20;

    long lineIndex = (long)((position.Y - 25) / _lineHeight) + _currentScrollLine;
    if (lineIndex < 0) return -1;

    long baseOffset = lineIndex * _bytesPerLine;
    int byteIndex = -1;

    
    if (position.X >= offsetColumnWidth + 10 && position.X < asciiColumnStart - 10)
    {
        byteIndex = (int)((position.X - offsetColumnWidth - 10) / (3 * _charWidth));
    }
    
    else if (position.X >= asciiColumnStart)
    {
        byteIndex = (int)((position.X - asciiColumnStart) / _charWidth);
    }

    if (byteIndex < 0 || byteIndex >= _bytesPerLine) return -1;
    
    long finalOffset = baseOffset + byteIndex;
    return (finalOffset >= 0 && finalOffset < _fileLength) ? finalOffset : -1;
}








 

    















    






    protected override Size MeasureOverride(Size availableSize)
{
    
    
    double width = double.IsInfinity(availableSize.Width) ? 1000 : availableSize.Width;
    double height = double.IsInfinity(availableSize.Height) ? 800 : availableSize.Height;
    
    return new Size(width, height);
}
    
    
    
    
    
        
    
    
    
    
    
    
    public Brush CurrentColor { get; set; } = Brushes.Green;
    
   private void CopyAsHex()
{
    if (!HasSelection) return;
    
    var count = SelectionMax - SelectionMin + 1;
    
    if (count > 10 * 1024 * 1024) count = 10 * 1024 * 1024; 

    var bytes = new byte[count];
    ReadBytes(SelectionMin, bytes); 

    string hex = BitConverter.ToString(bytes).Replace("-", " ");
    Clipboard.SetText(hex);
}

private void CopyAsCArray()
{
    if (!HasSelection) return;

    var count = SelectionMax - SelectionMin + 1;
    if (count > 1024 * 1024) count = 1024 * 1024; 

    var bytes = new byte[count];
    ReadBytes(SelectionMin, bytes);

    var sb = new System.Text.StringBuilder();
    sb.Append("byte[] data = { ");
    for (int i = 0; i < bytes.Length; i++)
    {
        sb.Append($"0x{bytes[i]:X2}");
        if (i < bytes.Length - 1) sb.Append(", ");
    }
    sb.Append(" };");
    Clipboard.SetText(sb.ToString());
}

private void CopyAsPlainText()
{
    if (!HasSelection) return;

    var count = SelectionMax - SelectionMin + 1;
    if (count > 10 * 1024 * 1024) count = 10 * 1024 * 1024;

    var bytes = new byte[count];
    ReadBytes(SelectionMin, bytes);

    var chars = new char[bytes.Length];
    for (int i = 0; i < bytes.Length; i++)
    {
        
        chars[i] = _asciiLookupTable[bytes[i]];
    }
    
    Clipboard.SetText(new string(chars));
}


protected override void OnMouseDown(MouseButtonEventArgs e)
{
    base.OnMouseDown(e);
    this.Focus(); 

    long clickedOffset = CalculateOffsetFromPosition(e.GetPosition(this));
    
    
    if (clickedOffset < 0 || clickedOffset >= _fileLength) return;

    bool isShiftPressed = Keyboard.Modifiers.HasFlag(ModifierKeys.Shift);

    if (isShiftPressed && _selectionStart != -1)
    {
        _selectionEnd = clickedOffset;
    }
    else
    {
        _selectionStart = clickedOffset;
        _selectionEnd = clickedOffset;
        _selectedOffset = clickedOffset;
    }

    OffsetSelected?.Invoke(this, _selectedOffset);
    InvalidateVisual();
}

protected override void OnMouseWheel(MouseWheelEventArgs e)
{
    base.OnMouseWheel(e);
    e.Handled = true;

    
    int multiplier = 1;
    if (Keyboard.Modifiers.HasFlag(ModifierKeys.Control)) multiplier = 100;
    else if (Keyboard.Modifiers.HasFlag(ModifierKeys.Shift)) multiplier = 1000;

    int linesToScroll = (e.Delta > 0 ? -3 : 3) * multiplier;
    
    long maxLines = _fileLength / _bytesPerLine;
    _currentScrollLine = Math.Clamp(_currentScrollLine + linesToScroll, 0, maxLines);

    InvalidateVisual();
}

    protected override void OnKeyDown(KeyEventArgs e)
    {
        base.OnKeyDown(e);
        
        
        var action = HotkeyManager.GetAction(Keyboard.Modifiers, e.Key);

        if (action == EUVAAction.CopyHex) { CopyAsHex(); e.Handled = true; }
        else if (action == EUVAAction.CopyCArray) { CopyAsCArray(); e.Handled = true; }
        else if (action == EUVAAction.CopyPlainText) { CopyAsPlainText(); e.Handled = true; }
        if (e.Key == Key.F3)
        {
            JumpToNextChange();
            e.Handled = true;
        }
    }
}
