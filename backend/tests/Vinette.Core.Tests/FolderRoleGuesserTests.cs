using Vinette.Core.Mail;

namespace Vinette.Core.Tests;

public sealed class FolderRoleGuesserTests
{
    private static MailFolderInfo Folder(string path, FolderRole role = FolderRole.Other, bool select = true)
    {
        var cut = path.LastIndexOf('/');
        return new MailFolderInfo(path, path[(cut + 1)..], cut < 0 ? null : path[..cut], role, select, 0, 0, true);
    }

    private static Dictionary<string, FolderRole> Roles(params MailFolderInfo[] folders) =>
        FolderRoleGuesser.Complete(folders).ToDictionary(f => f.Path, f => f.Role);

    [Fact]
    public void A_Server_Without_Special_Use_Is_Read_By_Dovecot_Style_Names()
    {
        var roles = Roles(Folder("INBOX", FolderRole.Inbox), Folder("Drafts"), Folder("Sent"), Folder("Junk"), Folder("Trash"), Folder("Archive"), Folder("Other"));

        Assert.Equal(FolderRole.Drafts, roles["Drafts"]);
        Assert.Equal(FolderRole.Sent, roles["Sent"]);
        Assert.Equal(FolderRole.Junk, roles["Junk"]);
        Assert.Equal(FolderRole.Trash, roles["Trash"]);
        Assert.Equal(FolderRole.Archive, roles["Archive"]);
        Assert.Equal(FolderRole.Other, roles["Other"]);
    }

    [Fact]
    public void Gmail_Style_Folders_Inside_A_Container_Are_Found()
    {
        var roles = Roles(
            Folder("INBOX", FolderRole.Inbox),
            Folder("[Gmail]", select: false),
            Folder("[Gmail]/Drafts"),
            Folder("[Gmail]/Sent Mail"),
            Folder("[Gmail]/Spam"),
            Folder("[Gmail]/Trash"));

        Assert.Equal(FolderRole.Drafts, roles["[Gmail]/Drafts"]);
        Assert.Equal(FolderRole.Sent, roles["[Gmail]/Sent Mail"]);
        Assert.Equal(FolderRole.Junk, roles["[Gmail]/Spam"]);
        Assert.Equal(FolderRole.Trash, roles["[Gmail]/Trash"]);
        Assert.Equal(FolderRole.Other, roles["[Gmail]"]);
    }

    [Fact]
    public void Russian_Names_And_Names_Under_Inbox_Are_Found_Regardless_Of_Case()
    {
        var roles = Roles(
            Folder("INBOX", FolderRole.Inbox),
            Folder("отправленные"),
            Folder("Черновики"),
            Folder("INBOX/Корзина"),
            Folder("Нежелательная почта"),
            Folder("Архив"));

        Assert.Equal(FolderRole.Sent, roles["отправленные"]);
        Assert.Equal(FolderRole.Drafts, roles["Черновики"]);
        Assert.Equal(FolderRole.Trash, roles["INBOX/Корзина"]);
        Assert.Equal(FolderRole.Junk, roles["Нежелательная почта"]);
        Assert.Equal(FolderRole.Archive, roles["Архив"]);
    }

    [Fact]
    public void Roles_Given_By_The_Server_Are_Never_Changed_Or_Duplicated()
    {
        var roles = Roles(
            Folder("INBOX", FolderRole.Inbox),
            Folder("Mail Sent", FolderRole.Sent), // the server says this is the sent folder
            Folder("Sent")); // by name alone it would be, but the role is taken

        Assert.Equal(FolderRole.Sent, roles["Mail Sent"]);
        Assert.Equal(FolderRole.Other, roles["Sent"]);
    }

    [Fact]
    public void A_Users_Own_Nested_Folder_Is_Not_Taken_For_A_Special_One()
    {
        var roles = Roles(Folder("INBOX", FolderRole.Inbox), Folder("Work/Archive"), Folder("Work/Trash"), Folder("Work"));

        Assert.All(roles.Values.Where(r => r != FolderRole.Inbox), r => Assert.Equal(FolderRole.Other, r));
    }

    [Fact]
    public void One_Folder_Per_Role_The_Plainer_Name_Wins()
    {
        var roles = Roles(Folder("INBOX", FolderRole.Inbox), Folder("Draft"), Folder("Drafts"));

        Assert.Equal(FolderRole.Drafts, roles["Drafts"]);
        Assert.Equal(FolderRole.Other, roles["Draft"]);
    }

    [Fact]
    public void A_Folder_That_Cannot_Hold_Messages_Gets_No_Role()
    {
        var roles = Roles(Folder("INBOX", FolderRole.Inbox), Folder("Trash", select: false));

        Assert.Equal(FolderRole.Other, roles["Trash"]);
    }

    [Fact]
    public void Nothing_To_Guess_Returns_The_Same_List()
    {
        var folders = new[] { Folder("INBOX", FolderRole.Inbox), Folder("Projects") };

        Assert.Same(folders, FolderRoleGuesser.Complete(folders));
    }
}
