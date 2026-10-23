using System;
using System.Linq;
using System.Collections.Generic;
using System.Threading.Tasks;
using AIFrontend.Pages;
using AIFrontend.Pages.OpenCode;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

// NS2: working-directory flyout — pure presentation + actual RefreshBlankPreferenceLabels / BlankProjectButton_Click.
internal static class DirectorySelectorTests
{
    private static int checks;
    private static void Check(bool ok,string message){checks++;if(!ok)throw new Exception(message);}
    internal const string LongDirectory=@"C:\Users\Zhang\source\repos\AIFrontend-history-20261020-lanes\a-very-long-working-directory-name-for-ellipsis";

    private static void Pure()
    {
        Check(OpenCodeDirectoryPresentation.Split(@"C:\repo\app")==("app",@"C:\repo"),"windows path splits into folder + parent");
        Check(OpenCodeDirectoryPresentation.Split(@"C:\repo\app\")==("app",@"C:\repo"),"trailing separator ignored");
        Check(OpenCodeDirectoryPresentation.Split(@"C:\app")==("app",@"C:\"),"drive root parent keeps separator");
        Check(OpenCodeDirectoryPresentation.Split(@"C:\")==(@"C:\",""),"drive root is its own name");
        Check(OpenCodeDirectoryPresentation.Split("/")==("/",""),"posix root is its own name");
        Check(OpenCodeDirectoryPresentation.Split("/different")==("different","/"),"posix top-level parent is root");
        Check(OpenCodeDirectoryPresentation.Split("/home/u/p")==("p","/home/u"),"posix nested");
        Check(OpenCodeDirectoryPresentation.Split(@"\\server\share\dir")==("dir",@"\\server\share"),"UNC nested");
        Check(OpenCodeDirectoryPresentation.Split("project")==("project",""),"bare name");
        Check(OpenCodeDirectoryPresentation.Split(null)==("","")&&OpenCodeDirectoryPresentation.Split("  ")==("",""),"empty");
        var (name,parent)=OpenCodeDirectoryPresentation.Split(LongDirectory);
        Check(parent+@"\"+name==LongDirectory,"split loses no characters of the full path");
        Check(OpenCodeDirectoryPresentation.AutomationName(LongDirectory)=="工作目录："+LongDirectory&&OpenCodeDirectoryPresentation.AutomationName(null)=="工作目录：未选择","automation name always carries full path");
    }

    private static void Shows(OpenCodePage p,string directory,string where)
    {
        var (name,parent)=OpenCodeDirectoryPresentation.Split(directory);
        Check(p.Preferences?.Directory==directory,"creation context uses chosen directory "+where);
        Check(p.ProjectName==name&&p.ProjectParent==parent,"folder name + parent shown "+where);
        Check(p.ProjectParentVisibility==(parent.Length==0?Visibility.Collapsed:Visibility.Visible),"parent line visibility "+where);
        Check(Equals(p.ProjectToolTip,directory)&&Equals(p.ProjectHelp,directory)&&Equals(p.ProjectAutomationName,"工作目录："+directory),"full path in ToolTip/HelpText/Name "+where);
    }

    internal static async Task Run(string xaml)
    {
        Pure();
        var p=new OpenCodePage(xaml);p.Load();p.AgentData();p.New();await p.WaitDefaults();
        var initial=p.Preferences!.Directory;Shows(p,initial,"initial");
        p.AddHomeProject(LongDirectory,"Long");
        var menu=p.ProjectMenu();
        Check(menu.Placement==Microsoft.UI.Xaml.Controls.Primitives.FlyoutPlacementMode.BottomEdgeAlignedLeft,"flyout opens below directory sentence");
        var panel=(StackPanel)menu.Content!;var list=panel.Children.OfType<ListView>().Single();
        var rows=list.Items.OfType<ListViewItem>().ToList();
        Check(rows.Count>=2&&list.SelectionMode==ListViewSelectionMode.Single,"projects use native single-selection ListView");
        Check(list.SelectedItem is ListViewItem{Tag:string selected}&&selected==initial,"current directory selected natively");
        var longRow=rows.Single(r=>Equals(r.Tag,LongDirectory));
        Check(!ReferenceEquals(list.SelectedItem,longRow)&&Equals(longRow.Attached.GetValueOrDefault("Tooltip"),LongDirectory),"long row carries full path tooltip");
        var search=panel.Children.OfType<AutoSuggestBox>().Single();search.Text="not-a-project";
        Check(list.Items.Count==0,"directory search filters actual project rows");search.Text="";
        list.ClickItem(longRow);await p.WaitAgentIdle();
        Shows(p,LongDirectory,"after switching to long directory");
        menu=p.ProjectMenu();list=((StackPanel)menu.Content!).Children.OfType<ListView>().Single();rows=list.Items.OfType<ListViewItem>().ToList();
        Check(list.SelectedItem is ListViewItem{Tag:LongDirectory},"reopened flyout keeps switched selection selected");
        var before=p.Preferences;var agent=p.AgentName;
        list.ClickItem(list.SelectedItem!);
        Check(ReferenceEquals(p.Preferences,before)&&p.AgentName==agent,"re-picking current directory keeps the draft context (agent/model/version) untouched");
        Shows(p,LongDirectory,"after re-picking current directory");
        Check(p.HttpMutations==0,"directory presentation/switch performs no config/send mutation");
        Console.WriteLine($"PASS B1a directory selector presentation/switch: {checks} assertions; native template/hover/flyout animation remain visual Gate");
    }
}
