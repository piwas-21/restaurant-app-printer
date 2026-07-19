using PrinterAPP.Models;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace PrinterAPP.Services;
public interface IPrinterService
{
    /// <summary>Absolute path of the active config file, for display in the UI.</summary>
    string ConfigFilePath { get; }

    Task<List<string>> GetAvailablePrintersAsync();
    Task<bool> PrintTestReceiptAsync(string printerName, PrinterConfiguration config);
    /// <summary>
    /// Probes the order printer-feed endpoint with the given API key. Returns the HTTP status
    /// (2xx = reachable and key accepted, 401 = key missing/incorrect, other = server error), or
    /// null when the host can't be reached. Replaces the old events-endpoint check that treated a
    /// 401 as success and so masked the missing-key failure the printer-feed actually hits.
    /// </summary>
    Task<System.Net.HttpStatusCode?> TestPrinterFeedAsync(string apiUrl, string? apiKey);
    Task<PrinterConfiguration> LoadConfigurationAsync();
    Task SaveConfigurationAsync(PrinterConfiguration config);
}
