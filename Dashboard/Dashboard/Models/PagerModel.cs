namespace Dashboard.Models;

/// <summary>Numbered pager: current page, total pages, total rows, page size and a link builder.</summary>
public sealed record PagerModel(int Page, int TotalPages, int TotalRows, int PageSize, Func<int, string> LinkFor)
{
    public int FirstRow => TotalRows == 0 ? 0 : (Page - 1) * PageSize + 1;
    public int LastRow => Math.Min(Page * PageSize, TotalRows);
}
