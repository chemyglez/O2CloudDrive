namespace O2CloudDrive.Auth;

public readonly record struct O2SessionValidationResult(
    O2SessionValidationStatus Status,
    O2Session? ValidatedSession = null);
