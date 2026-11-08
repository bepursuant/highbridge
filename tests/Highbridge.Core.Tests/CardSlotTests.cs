using Highbridge.Cards;

namespace Highbridge.Core.Tests;

public class CardSlotTests
{
    private const string Contact = "ACS ACR1281 1S Dual Reader ICC 0";
    private const string Contactless = "ACS ACR1281 1S Dual Reader PICC 0";
    private const string Sam = "ACS ACR1281 1S Dual Reader SAM 0";

    [Fact]
    public void Either_slot_of_one_reader_is_watched()
    {
        string[] readers = [Contact, Contactless, Sam];

        Assert.Equal([Contact, Contactless], CardSlots.WatchList(readers, null));
        Assert.Equal([Contact, Contactless], CardSlots.WatchList(readers, Contact));
        Assert.Equal([Contact, Contactless], CardSlots.WatchList(readers, Contactless));
        Assert.Equal([Sam], CardSlots.WatchList(readers, Sam));
        Assert.Null(CardSlots.WatchList(readers, "missing"));
    }

    [Fact]
    public void Contactless_names_with_cl_share_a_family_with_the_contact_slot()
    {
        const string tap = "Identiv uTrust 4701 F CL Reader 0";
        const string chip = "Identiv uTrust 4701 F Contact Reader 0";

        Assert.Equal([tap, chip], CardSlots.WatchList([tap, chip], chip));
        Assert.True(CardSlots.IsContactlessName(tap));
        Assert.True(CardSlots.IsContactName(chip));
        Assert.False(CardSlots.IsContactName(tap));
    }

    [Fact]
    public void A_present_card_in_the_other_slot_is_chosen()
    {
        Assert.Equal(Contactless, CardSlots.ChoosePresent([Contactless], CardInterface.Contact, Contact));
        Assert.Equal(Contact, CardSlots.ChoosePresent([Contact, Contactless], CardInterface.Contact, null));
        Assert.Equal(Contactless, CardSlots.ChoosePresent([Contact, Contactless], CardInterface.Contactless, Contact));
        Assert.Equal(Contact, CardSlots.ChoosePresent([Contact, Contactless], CardInterface.Auto, Contact));
        Assert.Equal(Contactless, CardSlots.ChoosePresent([Contact, Contactless], CardInterface.Auto, null));
    }

    [Fact]
    public void Each_interface_tries_its_directory_first_and_then_the_other()
    {
        Assert.Equal([CardInterface.Contact, CardInterface.Contactless], CardSlots.DirectoryOrder(CardInterface.Contact, Contactless));
        Assert.Equal([CardInterface.Contactless, CardInterface.Contact], CardSlots.DirectoryOrder(CardInterface.Contactless, Contact));
        Assert.Equal([CardInterface.Contactless, CardInterface.Contact], CardSlots.DirectoryOrder(CardInterface.Auto, Contactless));
        Assert.Equal([CardInterface.Contact, CardInterface.Contactless], CardSlots.DirectoryOrder(CardInterface.Auto, Contact));
        Assert.Equal([CardInterface.Contactless, CardInterface.Contact], CardSlots.DirectoryOrder(CardInterface.Auto, "Generic Reader 0"));
    }
}
