using System.Globalization;
using System.Security.Cryptography;
using System.Text.Json;

namespace Lmc.Web;

public sealed record Note(
    int Id, short BranchId, short DatabaseId, int Number, string Supplier, string Products,
    string? DocumentDate, string? IssueFieldDate, string? OperationalDate,
    int? ShiftId, int? ShiftNumber, string? ShiftDate, decimal? Volume,
    int Parcels, int Movements, bool Inconsistent, string? ReplicationDate)
{
    public int? GapDays => Rules.DayDifference(IssueFieldDate, OperationalDate);
    public bool GeneralShift => ShiftNumber == 0;
    public IReadOnlyList<string> Alerts(int threshold) => Rules.Alerts(this, threshold);
}

public sealed record Parcel(int Id, int ProductId, string Product, short TankId,
    string Tank, decimal? Volume, string? OperationalDate, string? ReplicationDate, string? FiscalSeal=null);
public sealed record Movement(int Id, int? ShiftId, string? OperationalDate, string? ReplicationDate);
public sealed record Shift(int Id, short BranchId, string Date, int Number)
{
    public bool IsGeneral => Number == 0;
}
public sealed record Book(int Id, int ProductId, string Product, string Date, int? Number);
public sealed record AllocationTank(short TankId,int ProductId,string Tank,bool Active,string? ActivatedAt);
public sealed record AllocationInput(int? ParcelId,int ProductId,short TankId,decimal? Volume);
public sealed record Detail(Note Note, IReadOnlyList<Parcel> Parcels, IReadOnlyList<Movement> Movements,
    IReadOnlyList<AllocationTank>? AllocationTanks=null)
{
    public string Version => Convert.ToHexString(SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(
        new { Note, Parcels, Movements, AllocationTanks })));
}
public sealed record PreviewRequest(string OperationalDate, int ShiftId, string? Reason, string Version,
    IReadOnlyList<AllocationInput>? Allocations=null);
public sealed record VolumeEffect(string Date, int ProductId, string Product, short TankId, string Tank, decimal Liters);
public sealed record Preview(string Status, bool CanApply, string Reason, string SourceDate, string TargetDate,
    int? SourceShiftId, Shift TargetShift, IReadOnlyList<VolumeEffect> Effects,
    IReadOnlyList<Book> BooksToReview, IReadOnlyList<string> Blockers, string Version,
    IReadOnlyList<BookReceipt>? BookReceipts = null,IReadOnlyList<AllocationInput>? Allocations=null);

public static class Rules
{
    public static DateOnly? Date(string? value) => DateOnly.TryParseExact(value, "yyyy-MM-dd",
        CultureInfo.InvariantCulture, DateTimeStyles.None, out var date) ? date : null;

    public static int? DayDifference(string? from, string? to) => Date(from) is { } a && Date(to) is { } b
        ? b.DayNumber - a.DayNumber : null;

    public static IReadOnlyList<string> Alerts(Note note, int threshold)
    {
        var alerts = new List<string>();
        if (note.GapDays >= threshold) alerts.Add("intervalo");
        if (note.GapDays < 0) alerts.Add("data_anterior");
        if (note.Inconsistent) alerts.Add("vinculos");
        if (note.OperationalDate is null || note.IssueFieldDate is null || note.DocumentDate is null)
            alerts.Add("data_ausente");
        if (note.ShiftId is null || note.ShiftNumber is null) alerts.Add("turno_ausente");
        if (!note.GeneralShift && note.ShiftDate is not null && note.ShiftDate != note.OperationalDate)
            alerts.Add("turno_data");
        return alerts;
    }

    public static IReadOnlyList<VolumeEffect> Effects(Detail detail, string targetDate)
    {
        if (detail.Note.OperationalDate is not { } sourceDate)
            throw new ArgumentException("A nota não tem data operacional de origem.");
        if (detail.Parcels.Any(p => p.Volume is null || p.OperationalDate != sourceDate))
            throw new ArgumentException("Há parcelas sem volume ou com datas divergentes. Confira os vínculos antes de simular.");
        if (sourceDate == targetDate) return [];
        return detail.Parcels.GroupBy(p => new { p.ProductId, p.Product, p.TankId, p.Tank })
            .SelectMany(g => new[] {
                new VolumeEffect(sourceDate, g.Key.ProductId, g.Key.Product, g.Key.TankId, g.Key.Tank, -g.Sum(p => p.Volume!.Value)),
                new VolumeEffect(targetDate, g.Key.ProductId, g.Key.Product, g.Key.TankId, g.Key.Tank, g.Sum(p => p.Volume!.Value))
            }).OrderBy(e => e.Date).ThenBy(e => e.TankId).ToArray();
    }
}
