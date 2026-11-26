using System.Windows;
using TapRoom.Models;

namespace TapRoom;

public partial class CaseDetailsWindow : Window
{
    public CaseDetailsWindow(CaptureSession session)
    {
        InitializeComponent();
        ReferenceBox.Text = session.CaseReference;
        NotesBox.Text = session.Notes;
    }
    public string CaseReference => ReferenceBox.Text;
    public string Notes => NotesBox.Text;
    private void Save_Click(object sender, RoutedEventArgs e) => DialogResult = true;
}
