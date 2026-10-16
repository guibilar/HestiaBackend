using System.Net;
using System.Net.Http.Json;
using Hestia.Modules.People.Application;
using Hestia.Modules.People.Contracts;
using Hestia.Testing;

namespace Hestia.Modules.People.Tests;

/// <summary>T105: contacts from a vCard file or a list: a preview, new people, merges that never overwrite, and choices.</summary>
public class ImportTests
{
    private const string Import = $"{PeopleHost.People}/import";

    // vCard 3.0 as iPhone and Google Contacts write it: folded lines, escapes, types, a group prefix, categories.
    private const string Google = """
        BEGIN:VCARD
        VERSION:3.0
        FN:Ana Souza
        N:Souza;Ana;;;
        NICKNAME:Aninha
        EMAIL;TYPE=INTERNET;TYPE=HOME:ana.home@example.com
        EMAIL;TYPE=INTERNET;TYPE=pref:ana@example.com
        TEL;TYPE=WORK:+1 403 555 0100
        item1.TEL;TYPE=CELL:+1 (403) 555-0199
        BDAY:1990-10-20
        NOTE:Met at the climbing gym\, Tuesdays.\nLikes tea.
        ORG:Acme Ltd.;Research
        TITLE:Chemist
        CATEGORIES:Friends,myContacts,Climbing
        PHOTO;ENCODING=b;TYPE=JPEG:/9j/4AAQSkZJRgABAQEASABIAAD/2wBDAAEBAQEBAQEBAQEBAQEBAQEBAQ
         EBAQEBAQEBAQEBAQEBAQE=
        END:VCARD
        BEGIN:VCARD
        VERSION:3.0
        N:Lima;Bruno;;;
        BDAY:--0315
        END:VCARD
        """;

    // vCard 2.1 as Android exports it: QUOTED-PRINTABLE with soft line breaks, and types without TYPE=.
    private const string Android = "BEGIN:VCARD\r\nVERSION:2.1\r\nN;CHARSET=UTF-8;ENCODING=QUOTED-PRINTABLE:Gon=C3=A7alves;Jo=C3=A3o;;;\r\n" +
        "FN;CHARSET=UTF-8;ENCODING=QUOTED-PRINTABLE:Jo=C3=A3o Gon=C3=A7al=\r\nves\r\nTEL;CELL:403-555-0123\r\nBDAY:19851231\r\nEND:VCARD\r\n";

    [Fact]
    public void TheReader_ReadsVCard3_And2_1()
    {
        var contacts = VCardReader.Read(Google + "\n" + Android).Select(c => c.Person).ToList();

        Assert.Equal(["Ana Souza", "Bruno Lima", "João Gonçalves"], contacts.Select(c => c.Name));
        var ana = contacts[0];
        // The preferred email first, the mobile phone first; the others go into the notes.
        Assert.Equal(("Aninha", "ana@example.com", "+1 (403) 555-0199"), (ana.Nickname, ana.Email, ana.Phone));
        Assert.Equal(new BirthdayDto(10, 20, 1990), ana.Birthday);
        Assert.Equal(["Friends", "Climbing"], ana.Groups);
        Assert.Equal("Met at the climbing gym, Tuesdays.\nLikes tea.\nWorks at Acme Ltd. (Chemist)\nOther emails: ana.home@example.com\n" +
            "Other phones: +1 403 555 0100", ana.Notes);
        Assert.Equal(new BirthdayDto(3, 15), contacts[1].Birthday);
        Assert.Equal(("403-555-0123", new BirthdayDto(12, 31, 1985)), (contacts[2].Phone, contacts[2].Birthday));
    }

    [Theory]
    [InlineData("1990-10-20", 10, 20, 1990)]
    [InlineData("19901020", 10, 20, 1990)]
    [InlineData("--1020", 10, 20, null)]
    [InlineData("--10-20", 10, 20, null)]
    [InlineData("1604-10-20", 10, 20, null)]
    [InlineData("1990-10-20T00:00:00Z", 10, 20, 1990)]
    public void Birthdays_InTheFormsThatPhonesWrite(string value, int month, int day, int? year) =>
        Assert.Equal(new BirthdayDto(month, day, year), VCardReader.Birthday(value));

    [Fact]
    public async Task APreview_ChangesNothing_ThenTheImport_AddsAndMerges_WithoutOverwriting()
    {
        await using var host = PeopleHost.Start();
        // Ana is there with another email and her phone in the local form, and a note of the user.
        var ana = await host.CreateAsync(new CreatePersonRequest("Ana S.", Phone: "403 555 0199", Notes: "My note", Groups: ["friends"]));
        await host.CreateAsync(new CreatePersonRequest("Bruno Lima", Birthday: new BirthdayDto(3, 15)));

        var preview = await PostAsync(host, new PeopleImportRequest(VCard: Google + "\n" + Android));
        Assert.Equal((true, 1, 1, 1, 0), (preview.DryRun, preview.Added, preview.Merged, preview.Unchanged, preview.Skipped));
        Assert.Equal(2, (await host.ListAsync()).TotalCount);

        var merge = preview.People[0];
        Assert.Equal((PeopleImportAction.Merge, ana.Person.Id, "phone"), (merge.Action, merge.ExistingId, merge.MatchedBy));
        Assert.Equal(["nickname", "email", "birthday", "groups"], merge.NewFields);
        Assert.Equal((PeopleImportAction.Same, "name"), (preview.People[1].Action, preview.People[1].MatchedBy));
        Assert.Equal((PeopleImportAction.Add, (Guid?)null), (preview.People[2].Action, preview.People[2].ExistingId));

        var result = await PostAsync(host, new PeopleImportRequest(VCard: Google + "\n" + Android, DryRun: false));
        Assert.Equal((false, 1, 1), (result.DryRun, result.Added, result.Merged));
        var joao = await host.GetAsync(result.People[2].ExistingId!.Value);
        Assert.Equal("João Gonçalves", joao.Person.Name);

        var merged = await host.GetAsync(ana.Person.Id);
        // The name, the phone, and the note of the user stay; the empty details are filled.
        Assert.Equal(("Ana S.", "403 555 0199", "My note", "Aninha", "ana@example.com"),
            (merged.Person.Name, merged.Person.Phone, merged.Notes, merged.Person.Nickname, merged.Person.Email));
        Assert.Equal(new BirthdayDto(10, 20, 1990), merged.Person.Birthday);
        Assert.Equal(["friends", "climbing"], merged.Person.Groups);

        // The same file again: nothing new.
        var again = await PostAsync(host, new PeopleImportRequest(VCard: Google + "\n" + Android, DryRun: false));
        Assert.Equal((0, 0, 3), (again.Added, again.Merged, again.Unchanged));
    }

    [Fact]
    public async Task OnlyTheChosenContacts_AreImported_AndABadOneIsSkippedWithTheReason()
    {
        await using var host = PeopleHost.Start();
        var people = new List<CreatePersonRequest>
        {
            new("Carla", Phone: "+1 403 555 0111"),
            new("  ", Email: "nobody@example.com"),
            new("Dan", Email: "not an email"),
            new("Eve")
        };

        var result = await PostAsync(host, new PeopleImportRequest(People: people, DryRun: false, Only: [0, 1, 2]));

        Assert.Equal([PeopleImportAction.Add, PeopleImportAction.Skip, PeopleImportAction.Skip, PeopleImportAction.Skip],
            result.People.Select(p => p.Action));
        Assert.Equal("Enter a name.", result.People[1].Reason);
        Assert.Contains("email", result.People[2].Reason);
        Assert.Equal("Not chosen.", result.People[3].Reason);
        Assert.Equal(["Carla"], (await host.ListAsync()).Items.Select(p => p.Name));
    }

    [Fact]
    public async Task TheSameContactTwiceInOneFile_IsAddedOnce()
    {
        await using var host = PeopleHost.Start();
        var people = new List<CreatePersonRequest> { new("Fay", Email: "fay@example.com"), new("Fay Wong", Email: "FAY@example.com", Nickname: "F") };

        var result = await PostAsync(host, new PeopleImportRequest(People: people, DryRun: false));

        Assert.Equal([PeopleImportAction.Add, PeopleImportAction.Merge], result.People.Select(p => p.Action));
        var fay = Assert.Single((await host.ListAsync()).Items);
        Assert.Equal(("Fay", "F"), (fay.Name, fay.Nickname));
    }

    [Theory]
    [InlineData("""{"dryRun":true}""")]
    [InlineData("""{"vCard":"hello","dryRun":true}""")]
    [InlineData("""{"vCard":"BEGIN:VCARD","people":[{"name":"A"}]}""")]
    public async Task ARequestWithoutContacts_OrWithBoth_IsRefused(string json)
    {
        await using var host = PeopleHost.Start();

        var response = await host.Client.PostAsync(Import, new StringContent(json, System.Text.Encoding.UTF8, "application/json"));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task AReadOnlyUser_CannotImport()
    {
        await using var host = PeopleHost.Start();
        using var readOnly = host.Factory.CreateClient();
        readOnly.DefaultRequestHeaders.Add(TestSignIn.ReadOnlyHeader, "yes");

        var response = await readOnly.PostAsJsonAsync(Import, new PeopleImportRequest(People: [new("Gus")]), HestiaApiFactory.Json);

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    private static async Task<PeopleImportResultDto> PostAsync(PeopleHost host, PeopleImportRequest request)
    {
        var response = await host.Client.PostAsJsonAsync(Import, request, HestiaApiFactory.Json);
        Assert.True(response.IsSuccessStatusCode, await response.Content.ReadAsStringAsync());
        return await response.ReadAsync<PeopleImportResultDto>();
    }
}
