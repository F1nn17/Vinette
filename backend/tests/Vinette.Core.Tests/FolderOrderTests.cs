using Vinette.Core.Mail;

namespace Vinette.Core.Tests;

public sealed class FolderOrderTests
{
    private static MailFolderInfo Folder(string path, FolderRole role = FolderRole.Other, string? parent = null) =>
        new(path, path[(path.LastIndexOf('/') + 1)..], parent, role, true, 0, 0, true);

    private static string[] Order(params MailFolderInfo[] folders) =>
        [.. FolderOrder.Sort(folders).Select(f => f.Path)];

    [Fact]
    public void Special_Folders_Come_First_In_A_Fixed_Order_Then_User_Folders_By_Name()
    {
        var order = Order(
            Folder("Zebra"),
            Folder("Trash", FolderRole.Trash),
            Folder("apple"),
            Folder("Sent", FolderRole.Sent),
            Folder("INBOX", FolderRole.Inbox),
            Folder("Junk", FolderRole.Junk),
            Folder("Archive", FolderRole.Archive),
            Folder("Drafts", FolderRole.Drafts));

        Assert.Equal(["INBOX", "Drafts", "Sent", "Archive", "Junk", "Trash", "apple", "Zebra"], order);
    }

    [Fact]
    public void Children_Follow_Their_Parent()
    {
        var order = Order(
            Folder("Work/Reports", parent: "Work"),
            Folder("Home"),
            Folder("Work"),
            Folder("Work/Archive", parent: "Work"),
            Folder("Work/Reports/2026", parent: "Work/Reports"));

        Assert.Equal(["Home", "Work", "Work/Archive", "Work/Reports", "Work/Reports/2026"], order);
    }

    [Fact]
    public void A_Folder_Whose_Parent_Is_Not_Listed_Is_Not_Lost()
    {
        var order = Order(Folder("INBOX", FolderRole.Inbox), Folder("Hidden/Child", parent: "Hidden"));

        Assert.Equal(["INBOX", "Hidden/Child"], order);
    }

    [Fact]
    public void An_Empty_List_Stays_Empty()
    {
        Assert.Empty(Order());
    }
}
