# Printer-App Security Audit

> Audit Date: 2026-04-03 | Risk Level: **CRITICAL**

---

## Summary

| Severity | Count |
|----------|-------|
| CRITICAL | 2 |
| HIGH | 5 |
| MEDIUM | 8 |
| LOW | 3 |
| **TOTAL** | **18** |

---

## CRITICAL Findings

### C1. No Signature Verification on Auto-Updates

**File:** `Services/UpdateService.cs` (lines 93-147)

**Issue:** Downloaded executables from GitHub Releases are NOT verified for authenticity. The app downloads an .exe, checks only that it's >1024 bytes, then directly executes it via a batch script. A MITM attacker can replace the update with malware.

**Attack vector:** Restaurant WiFi (often shared/less secure) -> intercept GitHub download -> replace with malicious .exe -> app installs and runs it with user privileges.

**Fix:**
- Implement Authenticode signature verification on downloaded files
- Verify file hash against a known hash published in the GitHub release
- Consider code-signing the .exe and verifying before install

---

### C2. No Certificate Pinning on API Connection

**File:** `Services/EventStreamingService.cs` (lines 123-135)

**Issue:** Standard `HttpClient` accepts ANY valid certificate. On a restaurant network, an attacker can intercept all order data including customer names, phone numbers, emails, payment info, and delivery addresses.

**Fix:** Implement `ServerCertificateCustomValidationCallback` to pin the backend certificate/public key hash.

---

## HIGH Findings

### H1. Unencrypted API Token Storage

**File:** Config stored at `%APPDATA%\KitchenPrinter\config.json`

API token (Bearer JWT) stored as plaintext JSON. Any application with read access to AppData can steal it.

**Fix:** Use Windows Data Protection API (DPAPI) to encrypt the token field, or use Windows Credential Manager.

---

### H2. HTTPS Not Enforced

**File:** `Models/PrinterConfiguration.cs` (line 13)

`ApiBaseUrl` defaults to HTTPS but can be changed to HTTP via UI. No validation rejects insecure protocols.

**Fix:** Validate `ApiBaseUrl` setter rejects non-HTTPS URLs (allow `http://localhost` for development only).

---

### H3. No API Response Validation

**File:** `Services/EventStreamingService.cs` (lines 305-408)

API responses deserialized directly without field validation. Malformed responses could cause crashes or injection.

**Fix:** Validate required fields after deserialization, add response size limits, sanitize string fields.

---

### H4. Order Data Not Sanitized Before Printing

**File:** `Services/OrderPrintService.cs` (lines 371-430)

Product names, customer names, quantities, and prices used directly from API without validation. Could cause formatting attacks or printer command injection.

**Fix:** Validate quantities (1-999), prices (>=0), and sanitize string fields (alphanumeric + Turkish chars + common punctuation).

---

### H5. Full PII Logged to UI

**File:** `Services/RequestLogService.cs` (lines 43-55)

Complete order JSON including customer names, phones, emails, delivery addresses, and payment info stored in in-memory log and displayed in app UI.

**Fix:** Log only order ID, table number, and total. Mask all PII fields. Don't store raw JSON.

---

## MEDIUM Findings

| # | Finding | File |
|---|---------|------|
| M1 | API token logged in debug output | `EventStreamingService.cs` |
| M2 | Config directory created with default (inherited) permissions | `WindowsPrinterService.cs` |
| M3 | GitHub release URL hardcoded (single point of compromise) | `UpdateService.cs` |
| M4 | Batch script injection risk in update installer | `UpdateService.cs` (lines 198-249) |
| M5 | Printer names not sanitized before P/Invoke calls | `OrderPrintService.cs` |
| M6 | Polling URL uses string interpolation (not URI-encoded) | `EventStreamingService.cs` |
| M7 | No file permissions restriction on config files | `WindowsPrinterService.cs` |
| M8 | Exception details exposed in user-facing error messages | Multiple services |

---

## LOW Findings

| # | Finding |
|---|---------|
| L1 | Temporary files (update .exe, test receipts) not guaranteed cleaned up |
| L2 | No connection timeout on SSE stream (Timeout.InfiniteTimeSpan) |
| L3 | Potential buffer issues with large order encoding |

---

## Positive Findings

- HTTPS as default API URL
- Lock-based thread safety for order deduplication
- Exponential backoff on connection failures (5s to 60s)
- Proper P/Invoke handle disposal in finally blocks
- Minimal dependencies (4 Microsoft packages, no third-party)
- App restart after update (not in-process replace)

---

## GDPR/PCI Compliance Concerns

The app handles PII (customer names, phones, emails, delivery addresses) and potentially payment data:
- Data not encrypted at rest (config, logs)
- PII visible in app UI logs
- No data retention policy (in-memory logs kept for session)
- No access control on the application itself

---

## Remediation Priority

### Immediate (Block Release)
1. Implement update signature verification (C1)
2. Add certificate pinning (C2)
3. Encrypt API token with DPAPI (H1)
4. Enforce HTTPS (H2)

### Before Production
5. Validate API responses (H3)
6. Sanitize order data before printing (H4)
7. Remove PII from logs (H5)

### Next Sprint
8. Fix all MEDIUM issues (M1-M8)
