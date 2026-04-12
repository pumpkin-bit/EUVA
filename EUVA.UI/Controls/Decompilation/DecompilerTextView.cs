// SPDX-License-Identifier: GPL-3.0-or-later

using System.IO.MemoryMappedFiles;
using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using EUVA.Core.Disassembly;
using EUVA.UI.Parsers;
using System.Text.RegularExpressions;

namespace EUVA.UI.Controls.Decompilation;

public sealed class DecompilerTextView : FrameworkElement, IDisposable
{
    private sealed class GlyphCache
    {
        private readonly Dictionary<char, byte[]?> _cache = new();
        private readonly GlyphTypeface _gtf;
        private readonly double _fontSize;
        private readonly int _cellW, _cellH;
        private readonly double _pixelsPerDip;

        public GlyphCache(GlyphTypeface gtf, double fontSize, int cellW, int cellH, double ppd)
        {
            _gtf = gtf; _fontSize = fontSize; _cellW = cellW; _cellH = cellH; _pixelsPerDip = ppd;
        }

        public byte[]? Get(char c)
        {
            if (_cache.TryGetValue(c, out var g)) return g;
            g = RasterizeGlyph(c);
            _cache[c] = g;
            return g;
        }

        public void Clear() => _cache.Clear();

        private byte[]? RasterizeGlyph(char c)
        {
            if (!_gtf.CharacterToGlyphMap.TryGetValue(c, out ushort gi)) return null;
            var gr = new GlyphRun(
                _gtf, 0, false, _fontSize, (float)_pixelsPerDip,
                new[] { gi }, new Point(0, _gtf.Baseline * _fontSize),
                new[] { _gtf.AdvanceWidths[gi] * _fontSize },
                null, null, null, null, null, null);
            var dv = new DrawingVisual();
            using (var dc = dv.RenderOpen()) { dc.DrawGlyphRun(Brushes.White, gr); }
            var rtb = new RenderTargetBitmap(_cellW, _cellH, 96 * _pixelsPerDip, 96 * _pixelsPerDip, PixelFormats.Pbgra32);
            rtb.Render(dv);
            var px = new byte[_cellW * _cellH * 4];
            rtb.CopyPixels(px, _cellW * 4, 0);
            bool empty = true;
            for (int i = 3; i < px.Length; i += 4) if (px[i] > 0) { empty = false; break; }
            return empty ? null : px;
        }
    }

    private double FontSize = 13;
    private double CharWidth = 8;
    private double LineHeight = 17;
    private const int PadLeft = 12;
    private const int PadTop = 8;
    private LayoutResult? _layout;
    private PseudocodeLine[] _flatLines = Array.Empty<PseudocodeLine>();
    private int[] _lineBlockIndex = Array.Empty<int>(); 
    private int[] _lineLocalIndex = Array.Empty<int>(); 
    private int _scrollLine;
    private int _cursorLine = -1; 
    private PseudocodeGenerator? _pseudoGen;
    private (int Row, int Col)? _selStart;
    private (int Row, int Col)? _selEnd;
    private bool _isSelecting;
    private long _layoutVersion = 0; 
    private double _pixelsPerDip = 1.0;
    private int CellW => (int)Math.Ceiling(CharWidth * _pixelsPerDip);
    private int CellH => (int)Math.Ceiling(LineHeight * _pixelsPerDip);
    private WriteableBitmap? _bitmap;
    private uint[] _backBuffer = Array.Empty<uint>();
    private int _bmpW, _bmpH;
    private bool _needsRedraw = true;
    private GlyphCache? _glyphCache;
    private readonly Image _image = new() { Stretch = Stretch.None };
    private uint _cBg, _cText, _cLineNum, _cBlockHeader, _cCursorLine;
    private uint _cKeyword, _cType, _cVariable, _cVariableAi, _cNumber, _cString;
    private uint _cFunction, _cOperator, _cPunct, _cComment, _cAddress, _cError, _cSelectionBg;

    public event EventHandler? RenameApplied;
    public event EventHandler<long>? LineClicked;

    public DecompilerTextView()
    {
        ClipToBounds = true; 
        Focusable = true;
        
        RenderOptions.SetBitmapScalingMode(_image, BitmapScalingMode.NearestNeighbor);
        RenderOptions.SetEdgeMode(_image, EdgeMode.Aliased);
        _image.SnapsToDevicePixels = true;
        AddVisualChild(_image); AddLogicalChild(_image);

        Loaded += (_, _) =>
        {
            _pixelsPerDip = VisualTreeHelper.GetDpi(this).PixelsPerDip;
            InitColors(); RebuildGlyphs();
            Redraw();
        };

        SizeChanged += (_, args) =>
        {
            int w = (int)Math.Max(1, ActualWidth * _pixelsPerDip);
            int h = (int)Math.Max(1, ActualHeight * _pixelsPerDip);
            
            if (w < 10 || h < 10) return; 

            ResizeBmp(w, h);
            Redraw();
        };

        IsVisibleChanged += (_, e) =>
        {
            if ((bool)e.NewValue)
            {
                Dispatcher.BeginInvoke(System.Windows.Threading.DispatcherPriority.ApplicationIdle, new Action(() =>
                {
                    Focus(); Keyboard.Focus(this);
                }));
            }
        };
    }

    protected override int VisualChildrenCount => 1;
    protected override Visual GetVisualChild(int index) => _image;
    protected override Size MeasureOverride(Size avail) { _image.Measure(avail); return avail; }
    protected override Size ArrangeOverride(Size final) { _image.Arrange(new Rect(final)); return final; }

    public void SetPseudocodeGenerator(PseudocodeGenerator gen) => _pseudoGen = gen;

    public void SetGraphData(LayoutResult? layout)
    {
        if (IsVisible)
        {
            Dispatcher.BeginInvoke(System.Windows.Threading.DispatcherPriority.ApplicationIdle, new Action(() =>
            {
                Focus(); Keyboard.Focus(this);
            }));
        }

        SetGraphDataAsync(layout);
    }

    private unsafe void SetGraphDataAsync(LayoutResult? layout)
    {
        _layout = layout;
        long myVersion = System.Threading.Interlocked.Increment(ref _layoutVersion);

        if (layout == null || layout.Nodes.Length == 0)
        {
            _flatLines = Array.Empty<PseudocodeLine>();
            _lineBlockIndex = Array.Empty<int>();
            _lineLocalIndex = Array.Empty<int>();
            _scrollLine = 0; _cursorLine = -1;
            Redraw();
            return;
        }

        var layoutSnapshot = layout;
        var pseudoGenSnapshot = _pseudoGen;

        System.Threading.Tasks.Task.Run(() =>
        {
            PseudocodeLine[] flatLines;
            int[] blkIdx, locIdx;
            try
            {
                var latestText = pseudoGenSnapshot != null ? pseudoGenSnapshot.DecompileFunction(null, null, 0, 0) : layoutSnapshot.FullText;
                FlattenToTextCore(layoutSnapshot, latestText, pseudoGenSnapshot, out flatLines, out blkIdx, out locIdx);
            }
            catch (Exception ex)
            {
                flatLines = new[] { 
                    new PseudocodeLine($"// DECOMPILATION CRASHED!", new[] { new PseudocodeSpan(0, 29, PseudocodeSyntax.Address) }),
                    new PseudocodeLine($"// Error: {ex.Message}", new[] { new PseudocodeSpan(0, 10 + ex.Message.Length, PseudocodeSyntax.Comment) }),
                    new PseudocodeLine($"// {ex.StackTrace?.Split('\n').FirstOrDefault()}", new[] { new PseudocodeSpan(0, 100, PseudocodeSyntax.Comment) })
                };
                blkIdx = new[] { -1, -1, -1 };
                locIdx = new[] { -1, -1, -1 };
            }

            Dispatcher.BeginInvoke(System.Windows.Threading.DispatcherPriority.Render, new Action(() =>
            {
                if (System.Threading.Interlocked.Read(ref _layoutVersion) != myVersion) return;

                _flatLines = flatLines;
                _lineBlockIndex = blkIdx;
                _lineLocalIndex = locIdx;
                _scrollLine = 0;
                _cursorLine = -1;
                Redraw();
            }));
        });
    }

    public void RefreshView()
    {
        if (_layout != null) SetGraphDataAsync(_layout);
        else Redraw();
    }

    public void OverrideText(PseudocodeLine[] newText)
    {
        System.Threading.Interlocked.Increment(ref _layoutVersion);
        _layout = null;
        _flatLines = newText;
        _lineBlockIndex = new int[newText.Length];
        _lineLocalIndex = new int[newText.Length];
        for (int i = 0; i < newText.Length; i++) {
            _lineBlockIndex[i] = 0;
            _lineLocalIndex[i] = i;
        }
        _scrollLine = 0;
        _cursorLine = -1;
        Dispatcher.BeginInvoke(System.Windows.Threading.DispatcherPriority.Render, new Action(() =>
        {
            Redraw();
        }));
    }

    public void JumpToAddress(long address)
    {
        if (_flatLines == null || _flatLines.Length == 0) return;
        for (int i = 0; i < _flatLines.Length; i++)
        {
            if (_flatLines[i].Address == address)
            {
                _cursorLine = i;
                int visLines = Math.Max(1, _bmpH / CellH);
                _scrollLine = Math.Max(0, i - visLines / 2);
                Redraw();
                return;
            }
        }
    }

    public void JumpNextAiChange()
    {
        if (_flatLines == null || _flatLines.Length == 0) return;

        int startLine = (_cursorLine + 1) % _flatLines.Length;
        for (int i = 0; i < _flatLines.Length; i++)
        {
            int lineIdx = (startLine + i) % _flatLines.Length;
            var line = _flatLines[lineIdx];
            if (line.Spans != null)
            {
                foreach (var span in line.Spans)
                {
                    if (span.Kind == PseudocodeSyntax.VariableAi)
                    {
                        _cursorLine = lineIdx;
                      
                        int visLines = Math.Max(1, _bmpH / CellH);
                        _scrollLine = Math.Max(0, lineIdx - visLines / 2);
                        Redraw();
                        return;
                    }
                }
            }
        }
    }

    private static void FlattenToTextCore(
        LayoutResult layout,
        PseudocodeLine[]? fullText,
        PseudocodeGenerator? pseudoGen,
        out PseudocodeLine[] flatLines,
        out int[] lineBlockIndex,
        out int[] lineLocalIndex)
    {
        var lines = new List<PseudocodeLine>(512);

        long funcAddr = layout.Nodes.Length > 0 ? layout.Nodes[0].StartOffset : 0;
        string originalName = $"sub_{funcAddr:X}";
        string funcName = originalName;

        if (pseudoGen != null && pseudoGen.TryGetGlobalRename(originalName, out var renamed))
            funcName = renamed.Name; 

        var primaryCtx = pseudoGen?.GetPrimaryClassContext();
        bool isClassMethod = primaryCtx != null && primaryCtx.Value.Confidence >= 0.8;

        string classVarName = primaryCtx?.VarName ?? "this";
        HashSet<ulong>? classFields = primaryCtx?.Fields;

        if (isClassMethod)
        {
            string className = $"Entity_{funcAddr:X}";
            if (pseudoGen != null && pseudoGen.TryGetGlobalRename("this_class", out var renamedClass))
                className = renamedClass.Name;

            lines.Add(new PseudocodeLine($"class {className} /* mapped from {classVarName} */ {{", new[] { 
                new PseudocodeSpan(0, 5, PseudocodeSyntax.Keyword),
                new PseudocodeSpan(6, className.Length, PseudocodeSyntax.Type)
            }));
            
            lines.Add(new PseudocodeLine("private:", new[] { new PseudocodeSpan(0, 8, PseudocodeSyntax.Keyword) }));
            
            if (classFields != null && classFields.Count > 0)
            {
                foreach (var offset in classFields.OrderBy(x => x))
                {
                    string fType = offset == 0 ? "void**" : "void*";
                    string fName = offset == 0 ? "vtable" : $"field_{offset:X}";
                    string fieldDef = $"    {fType} {fName}; // @ offset 0x{offset:X}";
                    lines.Add(new PseudocodeLine(fieldDef, new[] { 
                        new PseudocodeSpan(4, fType.Length, PseudocodeSyntax.Type),
                        new PseudocodeSpan(5 + fType.Length, fName.Length, PseudocodeSyntax.Variable),
                        new PseudocodeSpan(fieldDef.IndexOf("//"), fieldDef.Length - fieldDef.IndexOf("//"), PseudocodeSyntax.Comment)
                    }));
                }
            }
            else
            {
                string emptyDef = "    // No explicit fields accessed";
                lines.Add(new PseudocodeLine(emptyDef, new[] { new PseudocodeSpan(4, emptyDef.Length - 4, PseudocodeSyntax.Comment) }));
            }
            
            lines.Add(new PseudocodeLine("public:", new[] { new PseudocodeSpan(0, 7, PseudocodeSyntax.Keyword) }));
        }

       
        if (fullText != null)
        {
            foreach (var line in fullText)
            {
                if (isClassMethod)
                {
                  
                    var newSpans = new List<PseudocodeSpan>();
                    if (line.Spans != null)
                    {
                        foreach (var span in line.Spans)
                            newSpans.Add(new PseudocodeSpan(span.Start + 4, span.Length, span.Kind));
                    }
                    lines.Add(new PseudocodeLine("    " + line.Text, newSpans.ToArray()));
                }
                else
                {
                    lines.Add(line);
                }
            }
        }

        if (isClassMethod)
        {
            lines.Add(new PseudocodeLine("};", new[] { new PseudocodeSpan(0, 2, PseudocodeSyntax.Punctuation) }));
        }

      
        flatLines = lines.ToArray();
        lineBlockIndex = new int[flatLines.Length];
        lineLocalIndex = new int[flatLines.Length];
        for(int i = 0; i < flatLines.Length; i++) {
            lineBlockIndex[i] = 0;
            lineLocalIndex[i] = i;
            
            if (pseudoGen != null && flatLines[i].Address == -1) 
            {
                var c = pseudoGen.GetUserComment(0, i);
                if (c != null && !flatLines[i].Text.Contains(" // "))
                {
                    int commentStart = flatLines[i].Text.Length;
                    string commentText = " // " + c;
                    flatLines[i].Text += commentText;
                    
                    var newSpans = new List<PseudocodeSpan>(flatLines[i].Spans ?? Array.Empty<PseudocodeSpan>());
                    newSpans.Add(new PseudocodeSpan(commentStart, commentText.Length, PseudocodeSyntax.Comment));
                    flatLines[i].Spans = newSpans.ToArray();
                }
            }
        }
    }

    
    private void InitColors()
    {
        _cBg          = T("Background",        Color.FromRgb(0x1E, 0x1E, 0x2E)); 
        _cText        = T("ForegroundPrimary", Color.FromRgb(0xCD, 0xD6, 0xF4));
        _cLineNum     = T("ForegroundDisabled",Color.FromRgb(0x58, 0x5B, 0x70));
        _cBlockHeader = T("SeparatorLine",      Color.FromRgb(0x45, 0x47, 0x5A));
        _cCursorLine  = T("MenuHighlight",      Color.FromRgb(0x31, 0x32, 0x44)); 
        _cKeyword     = T("Accent",             Color.FromRgb(0x89, 0xB4, 0xFA)); 
        _cType        = T("PropertyKey",        Color.FromRgb(0xB4, 0xBE, 0xFE)); 
        _cVariable    = T("Hex_ByteSymbol",     Color.FromRgb(0xF5, 0xC2, 0xE7)); 
        _cVariableAi  = T("Hex_ByteHigh",       Color.FromRgb(0xCB, 0xA6, 0xF7)); 
        _cNumber      = T("PropertyValue",      Color.FromRgb(0xFA, 0xB3, 0x87)); 
        _cString      = T("Hex_AsciiPrintable", Color.FromRgb(0xA6, 0xE3, 0xA1)); 
        _cFunction    = T("StatusBarAccent",    Color.FromRgb(0x89, 0xB4, 0xFA)); 
        _cOperator    = T("TreeIconField",      Color.FromRgb(0x94, 0xE2, 0xD5)); 
        _cPunct       = T("HexOffset",          Color.FromRgb(0x6C, 0x70, 0x86)); 
        _cComment     = T("ForegroundDisabled", Color.FromRgb(0x6C, 0x70, 0x86)); 
        _cAddress     = T("ConsoleError",       Color.FromRgb(0xF3, 0x8B, 0xA8)); 
        _cError       = T("ConsoleError",       Color.FromRgb(0xF3, 0x8B, 0xA8)); 
        _cSelectionBg = T("Surface1",           Color.FromRgb(0x45, 0x47, 0x5A)); 
    }

    private static uint T(string key, Color fallback)
    {
        var res = Application.Current?.TryFindResource(key);
        Color c;
        if (res is SolidColorBrush scb) c = scb.Color;
        else if (res is Color col) c = col;
        else c = fallback;

        return 0xFF000000 | ((uint)c.R << 16) | ((uint)c.G << 8) | c.B;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static uint C(Color c) => (uint)((c.A << 24) | (c.R << 16) | (c.G << 8) | c.B);

    private void RebuildGlyphs()
    {
        var tf = new Typeface(new FontFamily("Consolas"), FontStyles.Normal, FontWeights.Normal, FontStretches.Normal);
        if (!tf.TryGetGlyphTypeface(out var gtf)) return;
        _glyphCache = new GlyphCache(gtf, FontSize, CellW, CellH, _pixelsPerDip);
    }

    private void ResizeBmp(int w, int h)
    {
        if (w == _bmpW && h == _bmpH) return;
        _bmpW = w; _bmpH = h;
        _bitmap = new WriteableBitmap(w, h, 96 * _pixelsPerDip, 96 * _pixelsPerDip, PixelFormats.Bgra32, null);
        _image.Source = _bitmap;
        _backBuffer = new uint[w * h];
        _needsRedraw = true;
    }

    private void Redraw() { _needsRedraw = true; InvalidateVisual(); }

    protected override void OnRender(DrawingContext dc)
    {
        if (_bmpW < 10 || _bmpH < 10) return;
        if (_glyphCache == null) { InitColors(); RebuildGlyphs(); }
        if (!_needsRedraw) { dc.DrawImage(_bitmap, new Rect(0, 0, ActualWidth, ActualHeight)); return; }
        _needsRedraw = false;

        Array.Fill(_backBuffer, _cBg);
        int visibleLines = _bmpH / CellH + 1;
        int lineNumWidth = 5; 

        for (int row = 0; row < visibleLines; row++)
        {
            int lineIdx = _scrollLine + row;
            if (lineIdx >= _flatLines.Length) break;
            int y = PadTop + row * CellH;
            if (y >= _bmpH) break;

            var line = _flatLines[lineIdx];

          
            int selStartRow = -1, selStartCol = -1, selEndRow = -1, selEndCol = -1;
            if (_selStart.HasValue && _selEnd.HasValue)
            {
                var s1 = _selStart.Value;
                var s2 = _selEnd.Value;
                if (s1.Row < s2.Row || (s1.Row == s2.Row && s1.Col < s2.Col))
                {
                    selStartRow = s1.Row; selStartCol = s1.Col;
                    selEndRow = s2.Row; selEndCol = s2.Col;
                }
                else
                {
                    selStartRow = s2.Row; selStartCol = s2.Col;
                    selEndRow = s1.Row; selEndCol = s1.Col;
                }
            }

            bool hasSelection = (selStartRow != -1 && (selStartRow != selEndRow || selStartCol != selEndCol));

        
            if (lineIdx == _cursorLine && !hasSelection && !string.IsNullOrEmpty(line.Text))
            {
                for (int fy = y; fy < y + CellH && fy < _bmpH; fy++) Array.Fill(_backBuffer, _cCursorLine, fy * _bmpW, _bmpW);
            }

            if (string.IsNullOrEmpty(line.Text)) continue;

            string lineNumStr = (lineIdx + 1).ToString().PadLeft(lineNumWidth);
            int x = PadLeft;
            for (int ci = 0; ci < lineNumStr.Length; ci++) Blit(x + ci * CellW, y, lineNumStr[ci], _cLineNum);

            int codeStartX = PadLeft + (lineNumWidth + 1) * CellW;

          
            if (hasSelection && lineIdx >= selStartRow && lineIdx <= selEndRow)
            {
                int startCol = (lineIdx == selStartRow) ? selStartCol : 0;
                int endCol = (lineIdx == selEndRow) ? selEndCol : line.Text.Length;
                
                if (startCol < 0) startCol = 0;
                if (endCol > line.Text.Length && lineIdx == selEndRow) endCol = line.Text.Length;
                else if (lineIdx < selEndRow) endCol = line.Text.Length + 1; 

                int len = endCol - startCol;
                if (len > 0)
                {
                    int pxStart = codeStartX + startCol * CellW;
                    int pxWidth = len * CellW;
                    if (pxStart < 0) pxStart = 0;
                    if (pxStart + pxWidth > _bmpW) pxWidth = _bmpW - pxStart;
                    
                    if (pxWidth > 0 && pxStart < _bmpW)
                    {
                        for (int fy = y; fy < y + CellH && fy < _bmpH; fy++)
                            Array.Fill(_backBuffer, _cSelectionBg, fy * _bmpW + pxStart, pxWidth);
                    }
                }
            }

            if (line.Spans != null && line.Spans.Length > 0)
            {
                foreach (var span in line.Spans)
                {
                    uint color = SyntaxColor(span.Kind);
                    for (int ci = 0; ci < span.Length && span.Start + ci < line.Text.Length; ci++)
                    {
                        int px = codeStartX + (span.Start + ci) * CellW;
                        if (px >= _bmpW) break;
                        Blit(px, y, line.Text[span.Start + ci], color);
                    }
                }
            }
            else
            {
                for (int ci = 0; ci < line.Text.Length; ci++)
                {
                    int px = codeStartX + ci * CellW;
                    if (px >= _bmpW) break;
                    Blit(px, y, line.Text[ci], _cText);
                }
            }
        }

        FlushBitmap();
        dc.DrawImage(_bitmap, new Rect(0, 0, ActualWidth, ActualHeight));
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private uint SyntaxColor(PseudocodeSyntax kind) => kind switch
    {
        PseudocodeSyntax.Keyword     => _cKeyword,
        PseudocodeSyntax.Type        => _cType,
        PseudocodeSyntax.Variable    => _cVariable,
        PseudocodeSyntax.VariableAi  => _cVariableAi,
        PseudocodeSyntax.Number      => _cNumber,
        PseudocodeSyntax.String      => _cString,
        PseudocodeSyntax.Function    => _cFunction,
        PseudocodeSyntax.Operator    => _cOperator,
        PseudocodeSyntax.Punctuation => _cPunct,
        PseudocodeSyntax.Comment     => _cComment,
        PseudocodeSyntax.Address     => _cError, 
        _                            => _cText,
    };

    protected override void OnMouseWheel(MouseWheelEventArgs e)
    {
        if (Keyboard.Modifiers == ModifierKeys.Control)
        {
            if (e.Delta > 0 && FontSize < 40)
            {
                FontSize += 1;
                CharWidth = FontSize * (8.0 / 13.0);
                LineHeight = FontSize * (17.0 / 13.0);
            }
            else if (e.Delta < 0 && FontSize > 6)
            {
                FontSize -= 1;
                CharWidth = FontSize * (8.0 / 13.0);
                LineHeight = FontSize * (17.0 / 13.0);
            }
            
            RebuildGlyphs();
            Redraw();
            e.Handled = true;
            return;
        }

        int delta = e.Delta > 0 ? -3 : 3;
        _scrollLine = Math.Clamp(_scrollLine + delta, 0, Math.Max(0, _flatLines.Length - 1));
        Redraw();
        e.Handled = true;
    }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        if (e.Key == Key.A && Keyboard.Modifiers == ModifierKeys.Control)
        {
            if (_flatLines.Length > 0)
            {
                _selStart = (0, 0);
                var lastLineIdx = _flatLines.Length - 1;
                var lastLineText = _flatLines[lastLineIdx].Text ?? "";
                _selEnd = (lastLineIdx, lastLineText.Length);
                _cursorLine = lastLineIdx;
                Redraw();
            }
            e.Handled = true;
            return;
        }

        if (e.Key == Key.C && Keyboard.Modifiers == ModifierKeys.Control)
        {
            CopySelectionToClipboard();
            e.Handled = true;
            return;
        }

        int visLines = Math.Max(1, _bmpH / CellH - 2);
        switch (e.Key)
        {
            case Key.Up:    _scrollLine = Math.Max(0, _scrollLine - 1); _cursorLine = Math.Max(0, _cursorLine - 1); ClearSelection(); break;
            case Key.Down:  _scrollLine = Math.Min(_flatLines.Length - 1, _scrollLine + 1); _cursorLine = Math.Min(_flatLines.Length - 1, _cursorLine + 1); ClearSelection(); break;
            case Key.PageUp:   _scrollLine = Math.Max(0, _scrollLine - visLines); _cursorLine = Math.Max(0, _cursorLine - visLines); ClearSelection(); break;
            case Key.PageDown: _scrollLine = Math.Min(_flatLines.Length - 1, _scrollLine + visLines); _cursorLine = Math.Min(_flatLines.Length - 1, _cursorLine + visLines); ClearSelection(); break;
            case Key.Home:  _scrollLine = 0; _cursorLine = 0; ClearSelection(); break;
            case Key.End:   _scrollLine = Math.Max(0, _flatLines.Length - 1); _cursorLine = _scrollLine; ClearSelection(); break;
            case Key.N:     PromptRename(); return;
            case Key.X:     ShowXrefs(); return;
            case Key.OemSemicolon: PromptComment(); return;
            default: base.OnKeyDown(e); return;
        }
        Redraw();
        e.Handled = true;
    }

    private void ClearSelection()
    {
        _selStart = null;
        _selEnd = null;
    }

    private void CopySelectionToClipboard()
    {
        if (!_selStart.HasValue || !_selEnd.HasValue) return;

        var s1 = _selStart.Value;
        var s2 = _selEnd.Value;
        
        int r1, c1, r2, c2;
        if (s1.Row < s2.Row || (s1.Row == s2.Row && s1.Col < s2.Col))
        {
            r1 = s1.Row; c1 = s1.Col;
            r2 = s2.Row; c2 = s2.Col;
        }
        else
        {
            r1 = s2.Row; c1 = s2.Col;
            r2 = s1.Row; c2 = s1.Col;
        }

        if (r1 == r2 && c1 == c2) return; 

        var sb = new System.Text.StringBuilder();
        for (int i = r1; i <= r2; i++)
        {
            if (i >= _flatLines.Length) break;
            var text = _flatLines[i].Text ?? "";
            
            int start = (i == r1) ? c1 : 0;
            int end = (i == r2) ? c2 : text.Length;
            
            if (start < 0) start = 0;
            if (end > text.Length) end = text.Length;
            if (start > text.Length) start = text.Length;
            if (start > end) start = end;

            sb.Append(text.Substring(start, end - start));
            if (i < r2) sb.AppendLine();
        }

        if (sb.Length > 0)
        {
            try { Clipboard.SetText(sb.ToString()); } catch { }
        }
    }

    private (int Row, int Col) GetTextPosition(Point pt)
    {
        int row = (int)((pt.Y * _pixelsPerDip - PadTop) / CellH);
        int lineIdx = _scrollLine + row;
        
        int lineNumWidth = 5;
        int codeStartX = PadLeft + (lineNumWidth + 1) * CellW;
        int col = (int)((pt.X * _pixelsPerDip - codeStartX) / CellW);
        if (col < 0) col = 0;
        return (lineIdx, col);
    }

    protected override void OnMouseDown(MouseButtonEventArgs e)
    {
        Focus();
        if (e.LeftButton == MouseButtonState.Pressed)
        {
            var pos = GetTextPosition(e.GetPosition(this));
            if (pos.Row >= 0 && pos.Row < _flatLines.Length)
            {
                var line = _flatLines[pos.Row];
                _cursorLine = pos.Row;
                _selStart = pos;
                _selEnd = pos;
                _isSelecting = true;
                CaptureMouse();
                Redraw();

                if (e.ClickCount == 1 && line.Address != -1)
                {
                    LineClicked?.Invoke(this, line.Address);
                }
                else if (e.ClickCount == 2)
                {
                    if (line.Spans != null)
                    {
                        foreach (var span in line.Spans)
                        {
                            if ((span.Kind == PseudocodeSyntax.Function || span.Kind == PseudocodeSyntax.Address) &&
                                pos.Col >= span.Start && pos.Col < span.Start + span.Length)
                            {
                                string symbol = line.Text.Substring(span.Start, span.Length);
                                HandleSymbolJump(symbol);
                                break;
                            }
                        }
                    }
                }
            }
        }
        e.Handled = true;
    }

    private void HandleSymbolJump(string symbol)
    {
        if (symbol.StartsWith("sub_") && ulong.TryParse(symbol.Substring(4), System.Globalization.NumberStyles.HexNumber, null, out ulong addr))
        {
            JumpRequest?.Invoke(this, (long)addr);
        }
        else if (symbol.StartsWith("loc_") && ulong.TryParse(symbol.Substring(4), System.Globalization.NumberStyles.HexNumber, null, out ulong laddr))
        {
            JumpRequest?.Invoke(this, (long)laddr);
        }
        else if (symbol.StartsWith("block_") && int.TryParse(symbol.Substring(6), out int bidx))
        {
            
            for (int i = 0; i < _flatLines.Length; i++)
            {
                if (_lineBlockIndex[i] == bidx && _lineLocalIndex[i] == 0)
                {
                    _cursorLine = i;
                    _scrollLine = Math.Max(0, i - 5);
                    Redraw();
                    break;
                }
            }
        }
    }

    public event EventHandler<long>? JumpRequest;

    protected override void OnMouseMove(MouseEventArgs e)
    {
        if (_isSelecting && e.LeftButton == MouseButtonState.Pressed)
        {
            var pos = GetTextPosition(e.GetPosition(this));
            if (pos.Row < 0) pos.Row = 0;
            if (pos.Row >= _flatLines.Length) pos.Row = _flatLines.Length - 1;
            
            if (_selEnd != pos)
            {
                _selEnd = pos;
                _cursorLine = pos.Row;
                Redraw();
            }
        }
        base.OnMouseMove(e);
    }

    protected override void OnMouseUp(MouseButtonEventArgs e)
    {
        if (_isSelecting)
        {
            _isSelecting = false;
            ReleaseMouseCapture();
        }
        base.OnMouseUp(e);
    }

    private void PromptRename()
    {
        if (_pseudoGen == null || _cursorLine < 0 || _cursorLine >= _flatLines.Length) return;
        var line = _flatLines[_cursorLine];
        if (line.Text == null) return;

        string? varName = null;
        if (line.Spans != null)
        {
            foreach (var span in line.Spans)
            {
                if ((span.Kind == PseudocodeSyntax.Variable || span.Kind == PseudocodeSyntax.VariableAi) && span.Start + span.Length <= line.Text.Length)
                {
                    varName = line.Text.Substring(span.Start, span.Length).Trim();
                    break;
                }
            }
        }
        if (varName == null || varName.Length == 0) return;

        var dlg = new Window
        {
            Title = "Rename Variable", Width = 350, Height = 130, WindowStartupLocation = WindowStartupLocation.CenterOwner,
            Background = (Brush)Application.Current.FindResource("Background"), ResizeMode = ResizeMode.NoResize
        };
        var sp = new StackPanel { Margin = new Thickness(10) };
        sp.Children.Add(new TextBlock { Text = $"Rename '{varName}' to:", Foreground = Brushes.White, Margin = new Thickness(0,0,0,5) });
        var tb = new TextBox { Text = varName, FontFamily = new FontFamily("Consolas"), FontSize = 13 };
        sp.Children.Add(tb);
        var okBtn = new Button { Content = "OK", Width = 80, Margin = new Thickness(0,8,0,0), HorizontalAlignment = HorizontalAlignment.Right };
        okBtn.Click += (_, _) => { dlg.DialogResult = true; dlg.Close(); };
        sp.Children.Add(okBtn);
        dlg.Content = sp;
        tb.SelectAll(); tb.Focus();

        if (dlg.ShowDialog() == true && !string.IsNullOrWhiteSpace(tb.Text) && tb.Text != varName)
        {
            _pseudoGen.ApplyRename(varName, tb.Text.Trim());
            RenameApplied?.Invoke(this, EventArgs.Empty);
        }
    }

    private void PromptComment()
    {
        if (_pseudoGen == null || _cursorLine < 0 || _cursorLine >= _flatLines.Length) return;
        if (_cursorLine >= _lineBlockIndex.Length || _cursorLine >= _lineLocalIndex.Length) return;

        var line = _flatLines[_cursorLine];

        int blockIdx = _lineBlockIndex[_cursorLine], localIdx = _lineLocalIndex[_cursorLine];
        long addr = line.Address;
        
        string? existing = addr != -1 ? _pseudoGen.GetCommentByAddress(addr) : _pseudoGen.GetUserComment(blockIdx, localIdx);
        var dlg = new Window
        {
            Title = "Line Comment", Width = 400, Height = 130, WindowStartupLocation = WindowStartupLocation.CenterOwner,
            Background = (Brush)Application.Current.FindResource("Background"), ResizeMode = ResizeMode.NoResize
        };
        var sp = new StackPanel { Margin = new Thickness(10) };
        sp.Children.Add(new TextBlock { Text = existing != null ? "Edit comment:" : "Add comment:", Foreground = Brushes.White, Margin = new Thickness(0,0,0,5) });
        var tb = new TextBox { Text = existing ?? "", FontFamily = new FontFamily("Consolas"), FontSize = 13 };
        sp.Children.Add(tb);
        var okBtn = new Button { Content = "OK", Width = 80, Margin = new Thickness(0,8,0,0), HorizontalAlignment = HorizontalAlignment.Right };
        okBtn.Click += (_, _) => { dlg.DialogResult = true; dlg.Close(); };
        sp.Children.Add(okBtn);
        dlg.Content = sp;
        tb.SelectAll(); tb.Focus();

        if (dlg.ShowDialog() == true)
        {
            string? comment = string.IsNullOrWhiteSpace(tb.Text) ? null : tb.Text.Trim();
            if (addr != -1) _pseudoGen.SetCommentByAddress(addr, comment);
            else _pseudoGen.SetUserComment(blockIdx, localIdx, comment);

            RefreshView();
            RenameApplied?.Invoke(this, EventArgs.Empty); 
        }
    }

    private void ShowXrefs()
    {
        if (_cursorLine < 0 || _cursorLine >= _flatLines.Length) return;
        var line = _flatLines[_cursorLine];
        if (line.Text == null) return;

        string? symbol = null;
        if (line.Spans != null)
        {
            foreach (var span in line.Spans)
            {
                if ((span.Kind == PseudocodeSyntax.Variable || span.Kind == PseudocodeSyntax.Function) && span.Start + span.Length <= line.Text.Length)
                {
                    symbol = line.Text.Substring(span.Start, span.Length).Trim();
                    if (symbol.Length > 0) break;
                    symbol = null;
                }
            }
        }
        if (symbol == null) return;

        var refs = new List<(int LineIdx, string Preview)>();
        for (int i = 0; i < _flatLines.Length; i++)
        {
            if (_flatLines[i].Text != null && _flatLines[i].Text!.Contains(symbol, StringComparison.Ordinal))
            {
                string preview = $"  {i + 1,5}:  {_flatLines[i].Text!.TrimStart()}";
                refs.Add((i, preview.Length > 80 ? preview.Substring(0, 77) + "..." : preview));
            }
        }
        if (refs.Count == 0) return;

        var dlg = new Window { Title = $"Xrefs to '{symbol}'", Width = 550, Height = 350, WindowStartupLocation = WindowStartupLocation.CenterOwner, Background = (Brush)Application.Current.FindResource("Background") };
        var listBox = new ListBox 
        { 
            Background = (Brush)Application.Current.FindResource("Background"), 
            Foreground = (Brush)Application.Current.FindResource("ForegroundPrimary"), 
            FontFamily = new FontFamily("Consolas"), FontSize = 12, BorderThickness = new Thickness(0) 
        };

        foreach (var r in refs) listBox.Items.Add(new ListBoxItem 
        { 
            Content = r.Preview, Tag = r.LineIdx, 
            Foreground = (Brush)Application.Current.FindResource("ForegroundPrimary"), 
            FontFamily = new FontFamily("Consolas") 
        });

        void Jump() { if (listBox.SelectedItem is ListBoxItem sel && sel.Tag is int targetLine) { _cursorLine = targetLine; _scrollLine = Math.Max(0, targetLine - 5); Redraw(); dlg.Close(); } }
        listBox.MouseDoubleClick += (_, _) => Jump();
        listBox.KeyDown += (_, ke) => { if (ke.Key == Key.Enter) Jump(); };
        dlg.Content = listBox; dlg.ShowDialog();
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private void Blit(int x, int y, char c, uint color)
    {
        if (_glyphCache == null) return;
        var glyph = _glyphCache.Get(c);
        if (glyph == null) return;

        int cw = CellW, ch = CellH;
        byte cr = (byte)(color >> 16), cg = (byte)(color >> 8), cb = (byte)color;

        for (int gy = 0; gy < ch; gy++)
        {
            int sy = y + gy;
            if (sy < 0 || sy >= _bmpH) continue;
            int dstRow = sy * _bmpW, srcRow = gy * cw;

            for (int gx = 0; gx < cw; gx++)
            {
                int sx = x + gx;
                if (sx < 0 || sx >= _bmpW) continue;

                byte alpha = glyph[(srcRow + gx) * 4 + 3];
                if (alpha == 0) continue;

                if (alpha >= 250) _backBuffer[dstRow + sx] = 0xFF000000 | ((uint)cr << 16) | ((uint)cg << 8) | cb;
                else
                {
                    uint dst = _backBuffer[dstRow + sx];
                    byte inv = (byte)(255 - alpha);
                    _backBuffer[dstRow + sx] = 0xFF000000 | ((uint)((cr * alpha + (byte)(dst >> 16) * inv) >> 8) << 16) | ((uint)((cg * alpha + (byte)(dst >> 8) * inv) >> 8) << 8) | (byte)((cb * alpha + (byte)dst * inv) >> 8);
                }
            }
        }
    }

    [MethodImpl(MethodImplOptions.AggressiveOptimization)]
    private void FlushBitmap()
    {
        if (_bitmap == null) return;
        _bitmap.Lock();
        try
        {
            unsafe { fixed (uint* src = _backBuffer) { Buffer.MemoryCopy(src, (void*)_bitmap.BackBuffer, _bmpW * _bmpH * 4, _bmpW * _bmpH * 4); } }
            _bitmap.AddDirtyRect(new Int32Rect(0, 0, _bmpW, _bmpH));
        }
        finally { _bitmap.Unlock(); }
    }

    public void Dispose() { _bitmap = null; _backBuffer = Array.Empty<uint>(); _glyphCache?.Clear(); }
}