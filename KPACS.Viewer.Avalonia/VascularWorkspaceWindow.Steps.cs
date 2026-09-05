namespace KPACS.Viewer;

/// <summary>
/// Schritt-Dispatcher des Vascular Workspace. Leitet den aktiven Schritt an das
/// zuständige Partial weiter und aktualisiert Sidebar-Header/-Text.
/// </summary>
public partial class VascularWorkspaceWindow
{
    private void OnStepActivated()
    {
        if (_mode == VascularWorkspaceMode.Tavi)
        {
            switch (_currentStep)
            {
                case 1:
                    ShowTaviDoubleObliqueStep();
                    break;
                default:
                    // Annulus (0), Ostien/C-Arm (2), Sizing/Bericht (3) sind in Phase G
                    // noch Platzhalter; Double-Oblique (1) ist als G2 ausgebaut.
                    SidebarHeader.Text = StepName(_mode, _currentStep);
                    SidebarBody.Text = "TAVI-Schritt ist als Phase G eingeplant und noch nicht aktiv.";
                    StepPanelHost.Content = null;
                    break;
            }

            return;
        }

        switch (_currentStep)
        {
            case 0:
                ShowSegmentationStep();
                break;
            case 1:
                ShowCenterlineStep();
                break;
            case 2:
                ShowMeasurementStep();
                break;
            case 3:
                ShowSizingStep();
                break;
            case 4:
                ShowReportStep();
                break;
        }
    }
}
