# Deployment guide

1. Build `PhotoBooth.sln` in Release.
2. Compile `Installer\PhotoBooth.iss` with Inno Setup 6.
3. Install camera vendor runtimes/drivers on the booth PC.
4. Open PhotoBooth Admin, connect the camera, pin frames, choose defaults, save a printer profile and test print.
5. Apply all Worker D1 migrations, including `0002_device_auth.sql`.
6. Store `ADMIN_API_KEY` as a Worker secret. This admin-only key must never be included in the desktop build or installer.
7. Deploy the frontend and replace `photo.example.com` in Worker CORS and installer defaults with its real HTTPS domain.
8. Create a 15-minute, one-time installation code with `POST /api/v1/admin/enrollment-codes`, authenticated using the admin secret.
9. Run the installer, enter the one-time code and both production URLs. The installer immediately exchanges the code for a device credential protected with Windows DPAPI.
10. Start Customer in kiosk mode and verify live view, capture, print, upload and QR from a phone.

Each device has an independent revocable credential. Never copy `device.credential` between Windows accounts or machines. Revoke a lost device by setting its `revoked_at` value in D1.

Back up `%LOCALAPPDATA%\PhotoBooth\Data` before upgrades. Use `IBackupService` for consistent application backups. Restore while both applications are stopped.

For all-day operation, disable Windows sleep/USB selective suspend, reserve at least 1 GB free disk, and inspect Diagnostics plus rotated logs before an event.
