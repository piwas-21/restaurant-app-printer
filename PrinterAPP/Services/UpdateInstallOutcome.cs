namespace PrinterAPP.Services;

/// <summary>
/// What happened when the user pressed "Update Now".
/// <para>Replaces a bare <c>bool</c>, which could not express the case that actually strands people:
/// on Android 8+ an APK install needs the per-app "Install unknown apps" permission, which a fresh
/// device has never granted. Declaring <c>REQUEST_INSTALL_PACKAGES</c> in the manifest only makes the
/// app *eligible* to ask. The old code returned <c>true</c> whenever <c>StartActivity</c> did not
/// throw, so a blocked install reported "Update successful! App will restart..." and then did
/// nothing — indistinguishable, to the operator, from a broken updater.</para>
/// </summary>
public enum UpdateInstallOutcome
{
    /// <summary>The installer was handed the artifact. On Android the system prompt takes over from here.</summary>
    Started,

    /// <summary>
    /// Android 8+ only: "Install unknown apps" is not granted for this app, so nothing was downloaded.
    /// The updater sends the user to that settings screen; they return and press Update Now again.
    /// </summary>
    PermissionRequired,

    /// <summary>The download or the hand-off to the installer failed. Details are in the error log.</summary>
    Failed
}
