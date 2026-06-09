using ClosedXML.Excel;
using EventWOS.Application.Attendance.Queries;

namespace EventWOS.Api.Excel;

/// <summary>
/// Phase D step 22: Excel exports for the admin <c>/attendance</c> page.
/// Two flavours — Logs (one row per attendance record) and Summary (one
/// row per event with rollup totals). Both share the same look:
/// indigo header band, autoFit columns, sheet-1 named after the sheet.
///
/// Files are streamed as <c>byte[]</c>; the controller wraps them in a
/// FileContentResult with the right mime + filename. No disk I/O.
/// </summary>
internal static class AttendanceExcelExporter
{
    private const string MimeXlsx =
        "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet";

    public static (byte[] Bytes, string Mime, string FileName) ExportLogs(
        IReadOnlyList<AttendanceListItemDto> rows)
    {
        using var wb = new XLWorkbook();
        var ws = wb.AddWorksheet("Attendance Logs");

        // Header
        string[] headers = { "Crew", "Event", "Action", "Recorded At (UTC)", "Location", "Recorded By" };
        for (int i = 0; i < headers.Length; i++)
        {
            var cell = ws.Cell(1, i + 1);
            cell.Value = headers[i];
            cell.Style.Font.Bold = true;
            cell.Style.Fill.BackgroundColor = XLColor.FromHtml("#4F46E5");  // indigo-600
            cell.Style.Font.FontColor = XLColor.White;
            cell.Style.Alignment.Horizontal = XLAlignmentHorizontalValues.Left;
        }

        // Body
        for (int i = 0; i < rows.Count; i++)
        {
            var r   = rows[i];
            var row = i + 2;
            ws.Cell(row, 1).Value = r.CrewName;
            ws.Cell(row, 2).Value = r.EventTitle;
            ws.Cell(row, 3).Value = HumaniseAction(r.Action);
            ws.Cell(row, 4).Value = r.RecordedAt;
            ws.Cell(row, 4).Style.DateFormat.Format = "yyyy-mm-dd hh:mm";
            ws.Cell(row, 5).Value = r.Location ?? "";
            ws.Cell(row, 6).Value = r.RecordedByName
                                    ?? (r.RecordedBy is null ? "" : r.RecordedBy);
        }

        ws.SheetView.FreezeRows(1);
        ws.Columns().AdjustToContents();

        using var ms = new MemoryStream();
        wb.SaveAs(ms);
        return (ms.ToArray(), MimeXlsx,
                $"attendance-logs-{DateTime.UtcNow:yyyyMMdd-HHmm}.xlsx");
    }

    public static (byte[] Bytes, string Mime, string FileName) ExportSummary(
        IReadOnlyList<EventAttendanceSummaryRow> rows)
    {
        using var wb = new XLWorkbook();
        var ws = wb.AddWorksheet("Event Summary");

        string[] headers =
        {
            "Event", "Venue", "Start", "End", "Status",
            "Capacity", "Crew Approved",
            "Checked In", "Checked Out", "Admin Overrides",
            "Attended", "No-Shows", "Attendance %"
        };
        for (int i = 0; i < headers.Length; i++)
        {
            var cell = ws.Cell(1, i + 1);
            cell.Value = headers[i];
            cell.Style.Font.Bold = true;
            cell.Style.Fill.BackgroundColor = XLColor.FromHtml("#4F46E5");
            cell.Style.Font.FontColor = XLColor.White;
        }

        for (int i = 0; i < rows.Count; i++)
        {
            var r   = rows[i];
            var row = i + 2;
            ws.Cell(row, 1).Value  = r.EventTitle;
            ws.Cell(row, 2).Value  = r.Venue;
            ws.Cell(row, 3).Value  = r.StartAt;
            ws.Cell(row, 3).Style.DateFormat.Format = "yyyy-mm-dd hh:mm";
            ws.Cell(row, 4).Value  = r.EndAt;
            ws.Cell(row, 4).Style.DateFormat.Format = "yyyy-mm-dd hh:mm";
            ws.Cell(row, 5).Value  = r.Status;
            ws.Cell(row, 6).Value  = r.MaxCrew;
            ws.Cell(row, 7).Value  = r.ConfirmedCrew;
            ws.Cell(row, 8).Value  = r.CheckedIn;
            ws.Cell(row, 9).Value  = r.CheckedOut;
            ws.Cell(row, 10).Value = r.AdminOverrides;
            ws.Cell(row, 11).Value = r.Attended;
            ws.Cell(row, 12).Value = r.NoShows;
            ws.Cell(row, 13).Value = (double)r.AttendancePercent / 100d;
            ws.Cell(row, 13).Style.NumberFormat.Format = "0.0%";
        }

        ws.SheetView.FreezeRows(1);
        ws.Columns().AdjustToContents();

        using var ms = new MemoryStream();
        wb.SaveAs(ms);
        return (ms.ToArray(), MimeXlsx,
                $"attendance-summary-{DateTime.UtcNow:yyyyMMdd-HHmm}.xlsx");
    }

    private static string HumaniseAction(string action) => action switch
    {
        "CheckIn"       => "Check In",
        "CheckOut"      => "Check Out",
        "AdminOverride" => "Admin Override",
        _               => action
    };
}
