// SPDX-License-Identifier: GPL-3.0-or-later

using System.Buffers;
using System.IO;
using System.IO.MemoryMappedFiles;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using EUVA.Core.Disassembly;

namespace EUVA.UI.Controls.Hex;


public enum DisasmDisplayMode { HexAndDisasm, DisasmOnly }

public sealed class DisassemblerHexView : FrameworkElement
{
    
    private sealed class GlyphCache
    {
        private readonly System.Collections.Concurrent.ConcurrentDictionary<long, uint[]> _cache
            = new(concurrencyLevel: 1, capacity: 2048);
        private readonly double _fontSize;
        private readonly int _cellW, _cellH;
        private readonly double _pixelsPerDip;

        public int CellW => _cellW;
        public int CellH => _cellH;

        public GlyphCache(GlyphTypeface gtf, double fontSize, int cellW, int cellH, double ppd)
        {
            _fontSize = fontSize; _cellW = cellW; _cellH = cellH; _pixelsPerDip = ppd;
        }

        public uint[] Get(char c, uint colorArgb)
        {
            long key = ((long)(ushort)c << 32) | colorArgb;
            return _cache.GetOrAdd(key, _ => RasterizeGlyph(c, colorArgb));
        }

        public void Clear() => _cache.Clear();

        private uint[] RasterizeGlyph(char c, uint colorArgb)
        {
            double dipW = _cellW / _pixelsPerDip;
            double dipH = _cellH / _pixelsPerDip;
            double dpi = 96.0 * _pixelsPerDip;
            byte r = (byte)(colorArgb >> 16), g = (byte)(colorArgb >> 8), b = (byte)colorArgb;
            var brush = new SolidColorBrush(Color.FromArgb(255, r, g, b)); brush.Freeze();

            var dv = new DrawingVisual();
            TextOptions.SetTextRenderingMode(dv, TextRenderingMode.Aliased);
            TextOptions.SetTextFormattingMode(dv, TextFormattingMode.Display);
            using (var dc = dv.RenderOpen())
            {
                dc.DrawRectangle(Brushes.Transparent, null, new Rect(0, 0, dipW, dipH));
                var tf = new Typeface(new FontFamily("Consolas"),
                    FontStyles.Normal, FontWeights.Normal, FontStretches.Normal);
                dc.DrawText(new FormattedText(c.ToString(),
                    System.Globalization.CultureInfo.InvariantCulture,
                    FlowDirection.LeftToRight, tf, _fontSize, brush, _pixelsPerDip),
                    new Point(0, 0));
            }
            var rtb = new RenderTargetBitmap(_cellW, _cellH, dpi, dpi, PixelFormats.Pbgra32);
            rtb.Render(dv);
            int stride = _cellW * 4;
            byte[] raw = new byte[_cellH * stride];
            rtb.CopyPixels(raw, stride, 0);
            var result = new uint[_cellW * _cellH];
            for (int i = 0; i < result.Length; i++)
            {
                byte pa = raw[i * 4 + 3];
                if (pa == 0) { result[i] = 0; continue; }
                result[i] = ((uint)pa << 24) | ((uint)raw[i * 4 + 2] << 16) |
                            ((uint)raw[i * 4 + 1] << 8) | raw[i * 4];
            }
            return result;
        }
    }

    private DisasmDisplayMode _displayMode = DisasmDisplayMode.HexAndDisasm;
    public DisasmDisplayMode DisplayMode
    {
        get => _displayMode;
        set { _displayMode = value; RecalcLayout(); Redraw(); }
    }


    
    private MemoryMappedViewAccessor? _accessor;
    private MemoryMappedFile? _mmf;
    private readonly ReaderWriterLockSlim _accessorLock = new(LockRecursionPolicy.NoRecursion);
    private long _fileLength;
    public long FileLength => _fileLength;

    
    private readonly DisassemblyEngine _engine = new(32);
    private DisassembledLine[]? _visibleLines;
    private int _visibleLineCount;

    
    
    
    
    private struct InstructionAnchor { public long Offset; public int Length; }
    private InstructionAnchor[] _anchors = new InstructionAnchor[512];
    private int _anchorCount;

    
    private long _topOffset;
    private long _selectedOffset = -1;

    
    private PeSectionInfo[] _peSections = Array.Empty<PeSectionInfo>();
    private int _peSectionCount;
    private long _entryPointFileOffset = -1;
    private bool _isMzFile;

    
    private const double LineHeight = 20;
    private const double CharWidth = 9;
    private const double FontSize = 13;
    private const int MaxInstructionBytes = 15;
    private const int MaxHexBytesShown = 16;  
    private const int HeaderHeight = 25;
    private const int BannerHeight = 22;

    private double _pixelsPerDip = 1.0;
    private int CellW => (int)Math.Ceiling(CharWidth * _pixelsPerDip);
    private int CellH => (int)Math.Ceiling(LineHeight * _pixelsPerDip);

    
    private int _offsetColPx;
    private int _hexStartPx;
    private int _asmStartPx;

    
    private WriteableBitmap? _bitmap;
    private uint[] _backBuffer = Array.Empty<uint>();
    private int _bmpW, _bmpH;
    private bool _needsRedraw = true;
    private GlyphCache? _glyphCache;
    private readonly Image _image = new() { Stretch = Stretch.None };

    
    private uint _cBg, _cOffset, _cByteNorm, _cByteNull, _cByteHigh, _cByteCtrl;
    private uint _cAsmMnem, _cAsmOp, _cHeaderBg, _cHeader, _cSep, _cCursorBg, _cCursorFg;
    private uint _cRowAlt, _cBannerBg, _cBannerFg;
    private uint _cAsmReg, _cAsmNum, _cAsmKw, _cAsmPunct;
    
    public event EventHandler<long>? OffsetSelected;
    public event EventHandler<long>? FindParentFunctionRequested;
    public event EventHandler<long>? XrefsRequested;

    public DisassemblerHexView()
    {
        ClipToBounds = true; Focusable = true;
        AddVisualChild(_image); AddLogicalChild(_image);

        Loaded += (_, _) =>
        {
            _pixelsPerDip = VisualTreeHelper.GetDpi(this).PixelsPerDip;
            InitColors(); RebuildGlyphs(); RecalcLayout(); Redraw();
        };
        SizeChanged += (_, _) =>
        {
            int w = (int)Math.Max(1, ActualWidth * _pixelsPerDip);
            int h = (int)Math.Max(1, ActualHeight * _pixelsPerDip);
            ResizeBmp(w, h); RecalcLayout(); Redraw();
        };
        InitContextMenu();
    }

    private static readonly SolidColorBrush MenuBg       = Freeze(new SolidColorBrush(Color.FromRgb(0x18, 0x18, 0x25)));
    private static readonly SolidColorBrush MenuBorder   = Freeze(new SolidColorBrush(Color.FromRgb(0x45, 0x47, 0x5A)));
    private static readonly SolidColorBrush MenuFg       = Freeze(new SolidColorBrush(Color.FromRgb(0xCD, 0xD6, 0xF4)));
    private static readonly SolidColorBrush MenuHoverBg  = Freeze(new SolidColorBrush(Color.FromRgb(0x31, 0x32, 0x44)));
    private static readonly SolidColorBrush MenuHoverFg  = Freeze(new SolidColorBrush(Color.FromRgb(0x89, 0xB4, 0xFA)));
    private static readonly SolidColorBrush MenuGestFg   = Freeze(new SolidColorBrush(Color.FromRgb(0xA6, 0xAD, 0xC8)));
    private static SolidColorBrush Freeze(SolidColorBrush b) { b.Freeze(); return b; }

    private void InitContextMenu()
    {
        var cm = new ContextMenu
        {
            Background = MenuBg,
            Foreground = MenuFg,
            BorderBrush = MenuBorder,
            BorderThickness = new Thickness(1),
            Padding = new Thickness(0, 4, 0, 4),
            HasDropShadow = true
        };

        var cmTemplate = new ControlTemplate(typeof(ContextMenu));
        var borderFactory = new FrameworkElementFactory(typeof(Border));
        borderFactory.SetValue(Border.BackgroundProperty, MenuBg);
        borderFactory.SetValue(Border.BorderBrushProperty, MenuBorder);
        borderFactory.SetValue(Border.BorderThicknessProperty, new Thickness(1));
        borderFactory.SetValue(Border.PaddingProperty, new Thickness(0, 4, 0, 4));
        borderFactory.SetValue(Border.SnapsToDevicePixelsProperty, true);

        var presenterFactory = new FrameworkElementFactory(typeof(ItemsPresenter));
        presenterFactory.SetValue(KeyboardNavigation.DirectionalNavigationProperty, KeyboardNavigationMode.Cycle);
        borderFactory.AppendChild(presenterFactory);

        cmTemplate.VisualTree = borderFactory;
        cm.Template = cmTemplate;

        cm.Items.Add(MakeItem("Go to Parent Function", "P / Enter",
            (_, _) => FindParentFunctionRequested?.Invoke(this, _selectedOffset)));
        cm.Items.Add(MakeItem("Find Xrefs", "X",
            (_, _) => XrefsRequested?.Invoke(this, _selectedOffset)));

        ContextMenu = cm;
    }

    private static MenuItem MakeItem(string header, string gesture, RoutedEventHandler onClick)
    {
        var mi = new MenuItem
        {
            Header = header,
            InputGestureText = gesture,
            Foreground = MenuFg,
            Background = Brushes.Transparent,
            BorderThickness = new Thickness(0),
        };

        var template = new ControlTemplate(typeof(MenuItem));
        var rootBorder = new FrameworkElementFactory(typeof(Border), "Bd");
        rootBorder.SetValue(Border.BackgroundProperty, Brushes.Transparent);
        rootBorder.SetValue(Border.PaddingProperty, new Thickness(8, 4, 8, 4));
        rootBorder.SetValue(Border.SnapsToDevicePixelsProperty, true);

        var grid = new FrameworkElementFactory(typeof(Grid));
        var col0 = new FrameworkElementFactory(typeof(ColumnDefinition));
        col0.SetValue(ColumnDefinition.WidthProperty, new GridLength(1, GridUnitType.Star));
        var col1 = new FrameworkElementFactory(typeof(ColumnDefinition));
        col1.SetValue(ColumnDefinition.WidthProperty, GridLength.Auto);
        grid.AppendChild(col0);
        grid.AppendChild(col1);

        var headerPresenter = new FrameworkElementFactory(typeof(ContentPresenter));
        headerPresenter.SetValue(ContentPresenter.ContentSourceProperty, "Header");
        headerPresenter.SetValue(FrameworkElement.VerticalAlignmentProperty, VerticalAlignment.Center);
        headerPresenter.SetValue(ContentPresenter.RecognizesAccessKeyProperty, true);
        grid.AppendChild(headerPresenter);

        var gestureText = new FrameworkElementFactory(typeof(TextBlock), "Gesture");
        gestureText.SetBinding(TextBlock.TextProperty, new System.Windows.Data.Binding("InputGestureText") { RelativeSource = new System.Windows.Data.RelativeSource(System.Windows.Data.RelativeSourceMode.TemplatedParent) });
        gestureText.SetValue(FrameworkElement.VerticalAlignmentProperty, VerticalAlignment.Center);
        gestureText.SetValue(FrameworkElement.MarginProperty, new Thickness(24, 0, 0, 0));
        gestureText.SetValue(TextBlock.ForegroundProperty, MenuGestFg);
        gestureText.SetValue(Grid.ColumnProperty, 1);
        grid.AppendChild(gestureText);

        rootBorder.AppendChild(grid);
        template.VisualTree = rootBorder;

        var hoverTrigger = new Trigger { Property = MenuItem.IsHighlightedProperty, Value = true };
        hoverTrigger.Setters.Add(new Setter(Border.BackgroundProperty, MenuHoverBg, "Bd"));
        hoverTrigger.Setters.Add(new Setter(MenuItem.ForegroundProperty, MenuHoverFg));
        hoverTrigger.Setters.Add(new Setter(TextBlock.ForegroundProperty, MenuHoverFg, "Gesture"));
        template.Triggers.Add(hoverTrigger);

        mi.Template = template;
        mi.Click += onClick;
        return mi;
    }

    protected override int VisualChildrenCount => 1;
    protected override Visual GetVisualChild(int index) => _image;
    protected override Size MeasureOverride(Size a)
    {
        double w = double.IsInfinity(a.Width) ? 1000 : a.Width;
        double h = double.IsInfinity(a.Height) ? 800 : a.Height;
        _image.Measure(new Size(w, h)); return new Size(w, h);
    }
    protected override Size ArrangeOverride(Size s) { _image.Arrange(new Rect(s)); return s; }

    

    public void SetDataSource(MemoryMappedFile mmf, MemoryMappedViewAccessor acc, long len)
    {
        _accessorLock.EnterWriteLock();
        try { _mmf = mmf; _accessor = acc; _fileLength = len; }
        finally { _accessorLock.ExitWriteLock(); }
        _topOffset = 0; _selectedOffset = -1;
        Redraw();
    }

    public void ScrollToOffset(long offset)
    {
        offset = Math.Clamp(offset, 0, Math.Max(0, _fileLength - 1));
        if (_accessor == null || _fileLength == 0) return;

        unsafe {
            byte* ptr = null;
            _accessor.SafeMemoryMappedViewHandle.AcquirePointer(ref ptr);
            try {
                long syncStart = Math.Max(0, offset - 64);
                long syncPoint = _engine.GetSyncOffset(ptr, (int)_fileLength, 0, syncStart, 64);
                
                _engine.FindInstructionEnclosing(ptr, (int)_fileLength, 0, syncPoint, offset, out long instrStart, out _);
                
                _topOffset = instrStart;
            } finally {
                _accessor.SafeMemoryMappedViewHandle.ReleasePointer();
            }
        }
        _selectedOffset = offset;
        Redraw();
    }

    public void SetBitness(int b) { _engine.Bitness = b; Redraw(); }
    public void RefreshView() => Redraw();
    public void RefreshBrushCache() { InitColors(); RebuildGlyphs(); Redraw(); }

    
    
    
    
    public void SetPeInfo(long entryPointFileOffset, PeSectionInfo[] sections, int sectionCount)
    {
        _entryPointFileOffset = entryPointFileOffset;
        _peSections = sections;
        _peSectionCount = sectionCount;
        
        _isMzFile = false;
        if (_accessor != null && _fileLength >= 2)
        {
            unsafe {
                byte* ptr = null;
                _accessor.SafeMemoryMappedViewHandle.AcquirePointer(ref ptr);
                try {
                    long off = -(long)_accessor.PointerOffset;
                    _isMzFile = ptr[off] == 0x4D && ptr[off + 1] == 0x5A;
                } finally { _accessor.SafeMemoryMappedViewHandle.ReleasePointer(); }
            }
        }
        Redraw();
    }

    public long EntryPointFileOffset => _entryPointFileOffset;
    public ReadOnlySpan<PeSectionInfo> PeSections => _peSections.AsSpan(0, _peSectionCount);

    

    private void InitColors()
    {
        _cBg       = C(ThC("Hex_Background",       Color.FromRgb(0x1E,0x1E,0x2E)));
        _cOffset   = C(ThC("HexOffset",            Color.FromRgb(0x6C,0x70,0x86)));
        _cByteNorm = C(ThC("Hex_ByteActive",       Color.FromRgb(0xCD,0xD6,0xF4)));
        _cByteNull = C(ThC("Hex_ByteNull",         Color.FromRgb(0x45,0x47,0x5A)));
        _cByteHigh = C(Color.FromRgb(0xB4,0x96,0xE8));
        _cByteCtrl = C(Color.FromRgb(0xC8,0x78,0x82));
        _cAsmMnem  = C(Color.FromRgb(0x89,0xB4,0xFA));
        _cAsmOp    = C(Color.FromRgb(0xA6,0xE3,0xA1));
        _cHeaderBg = C(ThC("Hex_HeaderBackground", Color.FromRgb(0x18,0x18,0x25)));
        _cHeader   = C(ThC("Hex_ColumnHeader",     Color.FromRgb(0x6C,0x70,0x86)));
        _cSep      = C(ThC("Hex_Separator",        Color.FromRgb(0x31,0x32,0x44)));
        _cCursorBg = C(ThC("Hex_CursorBackground", Color.FromRgb(0x89,0xB4,0xFA)));
        _cCursorFg = C(ThC("Hex_ByteSelected",     Color.FromRgb(0x1E,0x1E,0x2E)));
        _cRowAlt      = C(Color.FromArgb(0x10,0xCD,0xD6,0xF4));
        _cBannerBg    = C(Color.FromRgb(0x2A,0x2A,0x3E));
        _cBannerFg    = C(Color.FromRgb(0x89,0xb4,0xfa));
        
        _cAsmReg      = C(Color.FromRgb(0xF3,0x8B,0xA8)); 
        _cAsmNum      = C(Color.FromRgb(0xFA,0xB3,0x87)); 
        _cAsmKw       = C(Color.FromRgb(0xCB,0xA6,0xF7)); 
        _cAsmPunct    = C(Color.FromRgb(0x6C,0x70,0x86)); 
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static uint C(Color c) => ((uint)c.A<<24)|((uint)c.R<<16)|((uint)c.G<<8)|c.B;

    private static Color ThC(string key, Color fb)
    {
        var r = Application.Current?.TryFindResource(key);
        if (r is SolidColorBrush scb) return scb.Color;
        if (r is Color c) return c;
        return fb;
    }

    

    private void RebuildGlyphs()
    {
        if (_pixelsPerDip == 0) return;
        _glyphCache?.Clear();
        var tf = new Typeface(new FontFamily("Consolas"),
            FontStyles.Normal, FontWeights.Normal, FontStretches.Normal);
        tf.TryGetGlyphTypeface(out var gtf);
        _glyphCache = new GlyphCache(gtf, FontSize, CellW, CellH, _pixelsPerDip);
        System.Threading.Tasks.Task.Run(() =>
        {
            var gc = _glyphCache; if (gc == null) return;
            uint[] cols = { _cByteNorm, _cByteNull, _cByteHigh, _cByteCtrl,
                            _cOffset, _cHeader, _cAsmMnem, _cAsmOp, _cCursorFg };
            foreach (var col in cols)
                for (char c = ' '; c <= '~'; c++) gc.Get(c, col);
            Dispatcher.BeginInvoke(Redraw);
        });
    }

    

    private void RecalcLayout()
    {
        _offsetColPx = (int)(110 * _pixelsPerDip);
        _hexStartPx  = _offsetColPx + (int)(12 * _pixelsPerDip);

        if (_displayMode == DisasmDisplayMode.DisasmOnly)
        {
            _asmStartPx = _offsetColPx + (int)(16 * _pixelsPerDip);
        }
        else
        {
            int hexW = (int)(MaxHexBytesShown * 3 * CharWidth * _pixelsPerDip);
            _asmStartPx = _hexStartPx + hexW + (int)(16 * _pixelsPerDip);
        }
    }

    private int ViewportLines => _bmpH > 0 ? (int)(_bmpH / (LineHeight * _pixelsPerDip)) + 2 : 10;

    

    private void ResizeBmp(int w, int h)
    {
        if (w <= 0 || h <= 0 || (w == _bmpW && h == _bmpH)) return;
        _bmpW = w; _bmpH = h;
        _bitmap = new WriteableBitmap(w, h, 96*_pixelsPerDip, 96*_pixelsPerDip, PixelFormats.Bgra32, null);
        _backBuffer = new uint[w * h];
        _image.Source = _bitmap;
        _needsRedraw = true;
    }

    private void Redraw() { _needsRedraw = true; InvalidateVisual(); }

    

    protected override void OnRender(DrawingContext dc)
    {
        if (_bitmap == null || _bmpW == 0) return;

        if (_fileLength == 0 || _accessor == null)
        {
            Fill(_backBuffer, _bmpW, _bmpH, _cBg);
            Str("No file loaded — open a PE file, then View → Disassembler",
                (int)(10*_pixelsPerDip), _bmpH/2, _cHeader);
            Flush(); return;
        }
        if (_glyphCache == null) return;

        if (!_needsRedraw) return;
        _needsRedraw = false;

        unsafe {
            byte* ptr = null;
            _accessor.SafeMemoryMappedViewHandle.AcquirePointer(ref ptr);
            try {
                
                int reqLines = ViewportLines;
                if (_visibleLines == null || _visibleLines.Length < reqLines)
                {
                    if (_visibleLines != null) ArrayPool<DisassembledLine>.Shared.Return(_visibleLines, clearArray: true);
                    _visibleLines = ArrayPool<DisassembledLine>.Shared.Rent(reqLines + 10);
                }
                if (_anchors.Length < reqLines) _anchors = new InstructionAnchor[reqLines + 10];

                
                DecodeViewport(ptr);

                
                RenderFrame(ptr);
                Flush();
            } finally {
                _accessor.SafeMemoryMappedViewHandle.ReleasePointer();
            }
        }
    }

    private unsafe void DecodeViewport(byte* mapPtr)
    {
        int maxLines = Math.Min(ViewportLines, _visibleLines!.Length);
        int readLen = (int)Math.Min((long)maxLines * MaxInstructionBytes, _fileLength - _topOffset);
        if (readLen <= 0) { _visibleLineCount = 0; _anchorCount = 0; return; }

        long actualOffset = _topOffset - (long)_accessor!.PointerOffset;
        byte* readPtr = mapPtr + actualOffset;

        _visibleLineCount = _engine.DecodeVisible(readPtr, readLen, _topOffset, _visibleLines, maxLines);

        
        _anchorCount = _visibleLineCount;
        for (int i = 0; i < _visibleLineCount; i++)
        {
            _anchors[i].Offset = _visibleLines[i].Offset;
            _anchors[i].Length = _visibleLines[i].Length;
        }
    }

    private unsafe void RenderFrame(byte* mapPtr)
    {
        Fill(_backBuffer, _bmpW, _bmpH, _cBg);
        int hdrH = (int)(HeaderHeight * _pixelsPerDip);
        int bannerH = 0;

        
        FillRect(0, 0, _bmpW, hdrH, _cHeaderBg);
        Str("  Address", 0, (int)(5*_pixelsPerDip), _cHeader);
        if (_displayMode == DisasmDisplayMode.HexAndDisasm)
            Str("Hex Bytes", _hexStartPx, (int)(5*_pixelsPerDip), _cHeader);
        Str("Disassembly", _asmStartPx, (int)(5*_pixelsPerDip), _cHeader);
        FillRect(0, hdrH-(int)_pixelsPerDip, _bmpW, (int)_pixelsPerDip, _cSep);

        
        if (_isMzFile && _topOffset < 0x200)
        {
            bannerH = (int)(BannerHeight * _pixelsPerDip);
            FillRect(0, hdrH, _bmpW, bannerH, _cBannerBg);
            Str("  MZ Header detected  |  Use toolbar: sections / Entry Point",
                (int)(4 * _pixelsPerDip), hdrH + (int)(4 * _pixelsPerDip), _cBannerFg);
            FillRect(0, hdrH + bannerH - (int)_pixelsPerDip, _bmpW, (int)_pixelsPerDip, _cSep);
        }

        int contentTop = hdrH + bannerH;

        
        FillRect(_offsetColPx, 0, (int)_pixelsPerDip, _bmpH, _cSep);
        if (_displayMode == DisasmDisplayMode.HexAndDisasm)
            FillRect(_asmStartPx - (int)(8*_pixelsPerDip), 0, (int)_pixelsPerDip, _bmpH, _cSep);

        
        if (_visibleLines != null)
        {
            int row = 0; 
            int i = 0;
            while (i < _visibleLineCount)
            {
                RenderRow(i, contentTop, row, mapPtr);
                row++;
                i++;
            }
        }
    }

    private unsafe void RenderRow(int idx, int contentTop, int visualRow, byte* mapPtr)
    {
        ref var ln = ref _visibleLines![idx];
        
        int yPx = contentTop + (int)Math.Floor(visualRow * LineHeight * _pixelsPerDip);
        if (yPx + CellH > _bmpH) return;

        
        if (idx % 2 == 1)
            FillRect(0, yPx, _bmpW, CellH, _cRowAlt);

        
        bool rowSelected = _selectedOffset >= ln.Offset && _selectedOffset < ln.Offset + ln.Length;
        if (rowSelected)
            FillRect(0, yPx, _bmpW, CellH, (_cCursorBg & 0x00FFFFFF) | 0x20000000);

        

        
        Str($"{ln.Offset:X8}", (int)Math.Floor(8*_pixelsPerDip), yPx, _cOffset);

        if (_displayMode == DisasmDisplayMode.HexAndDisasm)
        {
            int hexStep = (int)Math.Floor(3 * CharWidth * _pixelsPerDip);
            int hexCellW = (int)Math.Floor(2 * CharWidth * _pixelsPerDip);
            long startActOff = ln.Offset - (long)_accessor!.PointerOffset;

            for (int b = 0; b < ln.Length && b < MaxHexBytesShown; b++)
            {
                if (ln.Offset + b >= _fileLength) break;
                byte v = mapPtr[startActOff + b];
                int xPx = _hexStartPx + b * hexStep;
                long byteAddr = ln.Offset + b;

                bool isCursor = (byteAddr == _selectedOffset);
                if (isCursor)
                    FillRect(xPx - 1, yPx, hexCellW + 2, CellH - 1, _cCursorBg);

                uint col = isCursor ? _cCursorFg : ByteColor(v);
                Blit(HexHi(v), xPx, yPx, col);
                Blit(HexLo(v), xPx + CellW, yPx, col);
            }

            if (ln.Length > MaxHexBytesShown)
                Blit('.', _hexStartPx + MaxHexBytesShown * hexStep, yPx, _cHeader);
        }

        
        fixed (char* txt = ln.TextBuffer)
        fixed (byte* cmap = ln.TextColorMap)
        {
            for (int ci = 0; ci < ln.TextLength; ci++)
            {
                uint col = SyntaxColor(cmap[ci]);
                Blit(txt[ci], _asmStartPx + ci * CellW, yPx, col);
            }
        }
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private uint SyntaxColor(byte cat) => cat switch
    {
        DisassembledLine.ColMnemonic    => _cAsmMnem,
        DisassembledLine.ColRegister    => _cAsmReg,
        DisassembledLine.ColNumber      => _cAsmNum,
        DisassembledLine.ColKeyword     => _cAsmKw,
        DisassembledLine.ColPunctuation => _cAsmPunct,
        _ => _cAsmOp,
    };

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private uint ByteColor(byte v)
    {
        if (v == 0) return _cByteNull;
        if (v < 0x20 || v == 0x7F) return _cByteCtrl;
        if (v > 0x7F) return _cByteHigh;
        return _cByteNorm;
    }


    

    protected override void OnMouseDown(MouseButtonEventArgs e)
    {
        base.OnMouseDown(e); Focus();
        if (e.ChangedButton == MouseButton.Right) return; 
        
        int hdrH = (int)(HeaderHeight * _pixelsPerDip);
        var pos = e.GetPosition(this);
        int lineIdx = (int)((pos.Y - hdrH / _pixelsPerDip) / LineHeight);
        if (_visibleLines == null || lineIdx < 0 || lineIdx >= _visibleLineCount) return;

        ref var ln = ref _visibleLines[lineIdx];
        long clicked;

        if (_displayMode == DisasmDisplayMode.HexAndDisasm)
        {
            double hexStartDip = _hexStartPx / _pixelsPerDip;
            if (pos.X >= hexStartDip && pos.X < hexStartDip + ln.Length * 3 * CharWidth)
            {
                int bi = (int)((pos.X - hexStartDip) / (3 * CharWidth));
                bi = Math.Clamp(bi, 0, ln.Length - 1);
                clicked = ln.Offset + bi;
            }
            else
                clicked = ln.Offset;
        }
        else
        {
            clicked = ln.Offset;
        }

        _selectedOffset = clicked;
        OffsetSelected?.Invoke(this, clicked);
        Redraw();
    }

    protected override void OnMouseWheel(MouseWheelEventArgs e)
    {
        base.OnMouseWheel(e); e.Handled = true;
        int mult = Keyboard.Modifiers.HasFlag(ModifierKeys.Control) ? 10
                 : Keyboard.Modifiers.HasFlag(ModifierKeys.Shift) ? 50 : 1;
        int instrCount = mult;

        if (e.Delta > 0) ScrollUpInstr(instrCount);
        else ScrollDownInstr(instrCount);
    }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        base.OnKeyDown(e);
        switch (e.Key)
        {
            case Key.PageDown: ScrollDownInstr(ViewportLines - 1); e.Handled = true; break;
            case Key.PageUp:   ScrollUpInstr(ViewportLines - 1);   e.Handled = true; break;
            case Key.Down:     ScrollDownInstr(1); e.Handled = true; break;
            case Key.Up:       ScrollUpInstr(1);   e.Handled = true; break;
            case Key.Home when Keyboard.Modifiers == ModifierKeys.Control:
                _topOffset = 0; Redraw(); e.Handled = true; break;
            case Key.End when Keyboard.Modifiers == ModifierKeys.Control:
                _topOffset = Math.Max(0, _fileLength - 64); Redraw(); e.Handled = true; break;
            case Key.P:
            case Key.Enter:
                FindParentFunctionRequested?.Invoke(this, _selectedOffset); e.Handled = true; break;
            case Key.X:
                XrefsRequested?.Invoke(this, _selectedOffset); e.Handled = true; break;
        }
    }

    
    
    
    private void ScrollDownInstr(int n)
    {
        if (_topOffset >= _fileLength - 1 || _accessor == null) return;

        if (n <= _anchorCount && _anchorCount > 0)
        {
            
            int idx = Math.Min(n, _anchorCount - 1);
            _topOffset = _anchors[idx].Offset;
        }
        else
        {
            unsafe {
                byte* ptr = null;
                _accessor.SafeMemoryMappedViewHandle.AcquirePointer(ref ptr);
                try {
                    long actualOffset = _topOffset - (long)_accessor.PointerOffset;
                    int readLen = (int)Math.Min((long)n * MaxInstructionBytes + 64, _fileLength - _topOffset);
                    if (readLen > 0)
                    {
                        int skip = _engine.SkipInstructions(ptr + actualOffset, readLen, _topOffset, n);
                        if (skip == 0) skip = 1;
                        _topOffset = Math.Min(_topOffset + skip, _fileLength - 1);
                    }
                } finally {
                    _accessor.SafeMemoryMappedViewHandle.ReleasePointer();
                }
            }
        }
        Redraw();
    }

    
    private void ScrollUpInstr(int n)
    {
        if (_topOffset <= 0 || _accessor == null) return;

        
        long safeStart = Math.Max(0, _topOffset - (long)n * 15 - 128);
        int readLen = (int)(_topOffset - safeStart);
        if (readLen <= 0) { _topOffset = 0; Redraw(); return; }

        unsafe {
            byte* ptr = null;
            _accessor.SafeMemoryMappedViewHandle.AcquirePointer(ref ptr);
            try {
                
                long syncedStart = _engine.GetSyncOffset(ptr, (int)_fileLength, 0, safeStart, 128);
                long actOff = syncedStart - (long)_accessor.PointerOffset;
                
                int lenToTop = (int)(_topOffset - syncedStart);
                byte* winPtr = ptr + actOff;
                
                int totalInstr = _engine.CountInstructions(winPtr, lenToTop, syncedStart, lenToTop);
                if (totalInstr <= n) {
                    _topOffset = syncedStart;
                } else {
                    int skipBytes = _engine.SkipInstructions(winPtr, lenToTop, syncedStart, totalInstr - n);
                    _topOffset = syncedStart + skipBytes;
                }
            } finally {
                _accessor.SafeMemoryMappedViewHandle.ReleasePointer();
            }
        }

        Redraw();
    }

    

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static char HexHi(byte v) => (char)(((v >> 4) < 10) ? '0' + (v >> 4) : 'A' + (v >> 4) - 10);
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static char HexLo(byte v) => (char)(((v & 0xF) < 10) ? '0' + (v & 0xF) : 'A' + (v & 0xF) - 10);

    private void Str(string text, int x, int y, uint col)
    {
        if (_glyphCache == null) return;
        for (int i = 0; i < text.Length; i++)
            Blit(text[i], x + i * CellW, y, col);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private void Blit(char c, int dx, int dy, uint color)
    {
        if (_glyphCache == null) return;
        var glyph = _glyphCache.Get(c, color);
        int cw = _glyphCache.CellW, ch = _glyphCache.CellH;
        int sx = 0, sy = 0, dw = cw, dh = ch;
        if (dx < 0) { sx -= dx; dw += dx; dx = 0; }
        if (dy < 0) { sy -= dy; dh += dy; dy = 0; }
        if (dx + dw > _bmpW) dw = _bmpW - dx;
        if (dy + dh > _bmpH) dh = _bmpH - dy;
        if (dw <= 0 || dh <= 0) return;

        int bufLen = _backBuffer.Length;
        for (int r = 0; r < dh; r++)
        {
            int dRow = (dy + r) * _bmpW + dx;
            int sRow = (sy + r) * cw + sx;
            for (int col = 0; col < dw; col++)
            {
                int dIdx = dRow + col;
                if ((uint)dIdx >= (uint)bufLen) continue;  
                uint sp = glyph[sRow + col];
                byte sa = (byte)(sp >> 24);
                if (sa == 0) continue;
                if (sa == 255) { _backBuffer[dIdx] = sp; continue; }
                uint dp = _backBuffer[dIdx];
                int ia = 255 - sa;
                _backBuffer[dIdx] = 0xFF000000u
                    | (uint)((((sp>>16)&0xFF) + (((dp>>16)&0xFF)*ia+127>>8)) << 16)
                    | (uint)((((sp>>8)&0xFF)  + (((dp>>8)&0xFF)*ia+127>>8))  << 8)
                    | (uint)(((sp&0xFF)       + ((dp&0xFF)*ia+127>>8)));
            }
        }
    }

    private void FillRect(int x, int y, int w, int h, uint color)
    {
        if (x < 0) { w += x; x = 0; }
        if (y < 0) { h += y; y = 0; }
        int x2 = Math.Min(x + w, _bmpW);
        int y2 = Math.Min(y + h, _backBuffer.Length / Math.Max(1, _bmpW));
        if (x >= x2 || y >= y2) return;
        byte sa = (byte)(color >> 24); if (sa == 0) return;
        byte sr = (byte)(color >> 16), sg = (byte)(color >> 8), sb = (byte)color;

        if (sa == 255)
        {
            uint solid = 0xFF000000u | ((uint)sr<<16) | ((uint)sg<<8) | sb;
            for (int r = y; r < y2; r++)
                _backBuffer.AsSpan(r * _bmpW + x, x2 - x).Fill(solid);
        }
        else
        {
            int ia = 255 - sa;
            for (int r = y; r < y2; r++)
            {
                int rs = r * _bmpW;
                for (int c = x; c < x2; c++)
                {
                    uint d = _backBuffer[rs + c];
                    _backBuffer[rs + c] = 0xFF000000u
                        | (uint)((sr*sa + (byte)(d>>16)*ia + 127) >> 8) << 16
                        | (uint)((sg*sa + (byte)(d>>8)*ia + 127) >> 8) << 8
                        | (uint)((sb*sa + (byte)d*ia + 127) >> 8);
                }
            }
        }
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static void Fill(uint[] buf, int w, int h, uint col)
        => buf.AsSpan(0, w * h).Fill(col | 0xFF000000u);

    private unsafe void Flush()
    {
        if (_bitmap == null) return;
        _bitmap.Lock();
        try
        {
            fixed (uint* src = _backBuffer)
                Buffer.MemoryCopy(src, (void*)_bitmap.BackBuffer,
                    (long)_bmpW * _bmpH * 4, (long)_bmpW * _bmpH * 4);
            _bitmap.AddDirtyRect(new Int32Rect(0, 0, _bmpW, _bmpH));
        }
        finally { _bitmap.Unlock(); }
    }

    public void Dispose()
    {
        _accessorLock.Dispose();
        if (_visibleLines != null)
        {
            ArrayPool<DisassembledLine>.Shared.Return(_visibleLines, clearArray: true);
            _visibleLines = null;
        }
    }
}
