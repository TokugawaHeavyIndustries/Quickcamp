using System.Runtime.InteropServices;

namespace QuickCamp;
public sealed class CheckListView : ListView
{
    public event EventHandler? HeaderCheckBoxClicked;

    private bool _headerChecked;

    public CheckListView()
    {
        DoubleBuffered = true; 
    }

    public bool HeaderChecked
    {
        get => _headerChecked;
        set
        {
            if (_headerChecked == value) return;
            _headerChecked = value;
            ApplyHeaderCheckBox();
        }
    }

    public void ApplyHeaderCheckBox()
    {
        if (!IsHandleCreated || Columns.Count == 0) return;

        var header = SendMessage(Handle, LVM_GETHEADER, IntPtr.Zero, IntPtr.Zero);
        if (header == IntPtr.Zero) return;

        var style = GetWindowLongPtr(header, GWL_STYLE).ToInt64();
        if ((style & HDS_CHECKBOXES) == 0)
            SetWindowLongPtr(header, GWL_STYLE, new IntPtr(style | HDS_CHECKBOXES));

        var item = new HDITEM { mask = HDI_FORMAT };
        SendMessage(header, HDM_GETITEMW, IntPtr.Zero, ref item);
        item.fmt |= HDF_CHECKBOX | HDF_FIXEDWIDTH;
        if (_headerChecked) item.fmt |= HDF_CHECKED;
        else item.fmt &= ~HDF_CHECKED;
        SendMessage(header, HDM_SETITEMW, IntPtr.Zero, ref item);
    }

    protected override void OnHandleCreated(EventArgs e)
    {
        base.OnHandleCreated(e);
        ApplyHeaderCheckBox();
    }

    protected override void WndProc(ref Message m)
    {
        if (m.Msg == WM_NOTIFY && m.LParam != IntPtr.Zero)
        {
            var nmhdr = Marshal.PtrToStructure<NMHDR>(m.LParam);
            if (nmhdr.code == HDN_ITEMSTATEICONCLICK)
            {
                HeaderCheckBoxClicked?.Invoke(this, EventArgs.Empty);
                return; 
            }
        }
        base.WndProc(ref m);
    }

    private const int WM_NOTIFY = 0x004E;
    private const int LVM_GETHEADER = 0x1000 + 31;
    private const int HDM_GETITEMW = 0x1200 + 11;
    private const int HDM_SETITEMW = 0x1200 + 12;
    private const int HDN_ITEMSTATEICONCLICK = -300 - 16;
    private const int GWL_STYLE = -16;
    private const long HDS_CHECKBOXES = 0x0400;
    private const uint HDI_FORMAT = 0x0004;
    private const int HDF_CHECKBOX = 0x0040;
    private const int HDF_CHECKED = 0x0080;
    private const int HDF_FIXEDWIDTH = 0x0100;

    [StructLayout(LayoutKind.Sequential)]
    private struct NMHDR
    {
        public IntPtr hwndFrom;
        public IntPtr idFrom;
        public int code;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct HDITEM
    {
        public uint mask;
        public int cxy;
        public IntPtr pszText;
        public IntPtr hbm;
        public int cchTextMax;
        public int fmt;
        public IntPtr lParam;
        public int iImage;
        public int iOrder;
        public uint type;
        public IntPtr pvFilter;
        public uint state;
    }

    [DllImport("user32.dll", EntryPoint = "SendMessageW")]
    private static extern IntPtr SendMessage(IntPtr hWnd, int msg, IntPtr wParam, IntPtr lParam);

    [DllImport("user32.dll", EntryPoint = "SendMessageW")]
    private static extern IntPtr SendMessage(IntPtr hWnd, int msg, IntPtr wParam, ref HDITEM lParam);

    [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW")]
    private static extern IntPtr GetWindowLongPtr(IntPtr hWnd, int index);

    [DllImport("user32.dll", EntryPoint = "SetWindowLongPtrW")]
    private static extern IntPtr SetWindowLongPtr(IntPtr hWnd, int index, IntPtr value);
}
