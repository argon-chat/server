/**
 * The i18n keys for the refusals the server sends as codes.
 *
 * A code this build does not know gets the generic key of its kind rather than whatever text came
 * with it, which during a rolling deploy may still be an English sentence.
 */

const REGISTRATION: Record<string, { field?: string; key: string }> = {
    EMAIL_ALREADY_REGISTERED: { field: "email", key: "register_error_email_taken" },
    USERNAME_ALREADY_TAKEN: { field: "username", key: "register_error_username_taken" },
    USERNAME_RESERVED: { field: "username", key: "register_error_username_reserved" },
    EMAIL_BANNED: { field: "email", key: "register_error_email_banned" },
    SSO_EMAILS_NOT_ALLOWED: { field: "email", key: "register_error_sso_email" },
    REGION_BANNED: { key: "register_error_region_banned" },
    INTERNAL_ERROR: { key: "register_failed_desc" },
};

/** A validation refusal names the field it is about. */
const REGISTRATION_FIELD: Readonly<Record<string, string>> = {
    email: "register_error_invalid_email",
    username: "register_error_invalid_username",
    password: "register_error_invalid_password",
    displayName: "register_error_invalid_displayName",
    birthDate: "register_error_invalid_birthDate",
    argreeTos: "register_error_invalid_argreeTos",
};

/** `LoginDenial` on the server. */
const ACCESS_DENIAL: Readonly<Record<string, string>> = {
    app_not_found: "access_denied_app_not_found",
    unapproved_app_team_only: "access_denied_unapproved_app_team_only",
    internal_app_team_only: "access_denied_internal_app_team_only",
    internal_app_staff_only: "access_denied_internal_app_staff_only",
    operator_missing: "access_denied_operator_missing",
    operator_inactive: "access_denied_operator_inactive",
    operator_no_app_access: "access_denied_operator_no_app_access",
};

/** `OperatorAuthController`: `reason` when the certificate was refused, otherwise `error`. */
const OPERATOR_REFUSAL: Readonly<Record<string, string>> = {
    no_certificate: "operator_error_no_certificate",
    certificate_not_trusted: "operator_error_certificate_not_trusted",
    certificate_revoked: "operator_error_certificate_revoked",
    certificate_user_mismatch: "operator_error_certificate_user_mismatch",
    operator_inactive: "operator_error_operator_inactive",
    operator_not_found: "operator_error_operator_not_found",
    certificate_unverified: "operator_error_certificate_unverified",
};

/** A refused registration, and the field it belongs under when one owns it. */
export function registrationErrorKey(error: string, field?: string | null): { field?: string; key: string } {
    const known = REGISTRATION[error];
    if (known) return known;
    if (!field) return { key: "register_failed" };
    if (field === "birthDate" && error === "invalid_request") return { field, key: "dob_required" };
    return { field, key: REGISTRATION_FIELD[field] ?? "register_failed" };
}

/** Why the application turned this account away. */
export function accessDenialKey(reason?: string | null): string {
    return (reason && ACCESS_DENIAL[reason]) || "access_denied_desc";
}

/** Why the operator certificate was refused. */
export function operatorRefusalKey(result: { error?: string; reason?: string }): string {
    const code = result.reason ?? result.error;
    return (code && OPERATOR_REFUSAL[code]) || "operator_cert_failed_desc";
}
