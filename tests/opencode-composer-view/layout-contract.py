"""Production XAML/source contract. Not a native layout/IME/focus measurement."""
from pathlib import Path
import xml.etree.ElementTree as ET

root = Path(__file__).resolve().parents[2]
page = root / "src/AIFrontend/Pages/OpenCodePage.xaml"
tree = ET.parse(page)
xname = "{http://schemas.microsoft.com/winfx/2006/xaml}Name"
nodes = {n.attrib[xname]: n for n in tree.iter() if xname in n.attrib}
parents = {child: parent for parent in tree.iter() for child in parent}
editor, actions, leading = (nodes[n] for n in ("Composer", "ComposerActions", "ComposerLeadingHost"))
assert parents[editor] is parents[actions]
assert editor.attrib["Grid.Column"] == "0" and actions.attrib["Grid.Column"] == "1"
assert parents[editor].attrib["ColumnDefinitions"] == "*,Auto", "no obsolete fixed gutter/reserve"
assert leading.attrib["Grid.Column"] == "0" and parents[editor].attrib["Grid.Column"] == "1"
assert parents[leading] is parents[parents[editor]], "attachment must stay outside editor's grid"
assert editor.attrib["MinHeight"] == "40" and editor.attrib["MaxHeight"] == "160"
assert editor.attrib["AcceptsReturn"] == "True" and editor.attrib["TextWrapping"] == "Wrap"
assert editor.attrib["ScrollViewer.VerticalScrollBarVisibility"] == "Auto"
assert "Padding" not in editor.attrib
source = (root / "src/AIFrontend/Pages/OpenCodePage.xaml.cs").read_text(encoding="utf-8-sig")
for obsolete in ("ComposerOverlayReserve", "_composerBasePadding", "_composerMultiLine", "UpdateComposerMultiLineLayout",
                 "Grid.SetColumn(ComposerActions", "Composer.Padding ="):
    assert obsolete not in source, f"obsolete multiline inward layout: {obsolete}"
assert "Composer.TextCompositionStarted += Composer_TextCompositionStarted" in source
assert "Composer.TextCompositionEnded += Composer_TextCompositionEnded" in source
assert "IsComposerSessionWorking(selected)" in source
assert "historyEpoch == _messageHistoryEpoch" in source and "lease.CanControlSession()" in source
assert "BeginComposerInterruptProtection(lease, operationId, fingerprint, ref protectedMutation)" in source
assert "if (!protectedMutation) return;" in source, "missing parent protection hookup must fail closed"
assert source.index("BeginComposerInterruptProtection(lease") < source.index("pending = client.InterruptGuardedAsync(sessionId,")
assert "lease.CompleteSessionControl(operationId, NotificationInteractionStatus.Succeeded)" in source
print("PASS composer production XAML/source: stable external attachment/send columns at one/two/many lines; no inward moves or reserve; IME/scroll retained; protected selected-session interrupt")
