using Hestia.Modules.Search.Contracts;

namespace Hestia.Modules.People.Application;

/// <summary>The People hits of the search (T17): people whose name, nickname, relationship, groups, email, phone, or notes have the text.</summary>
public sealed class PeopleSearch(PersonService people) : ISearchSource
{
    public string Module => "People";

    public int Order => 130;

    public async Task<SearchHits> SearchAsync(string text, int limit, CancellationToken cancellationToken)
    {
        var page = await people.ListAsync(new PersonQuery(null, text, null, 1, limit), cancellationToken);
        return new SearchHits(page.Items.Select(p => new SearchHit(
            $"people.person.{p.Id:D}", Module, "person", p.Name, p.Relationship ?? p.Nickname, null, null,
            $"/people/{p.Id:D}")).ToList(), page.TotalCount);
    }
}
