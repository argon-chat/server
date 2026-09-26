import { AppManagementError, TeamConsoleError } from "@/lib/glue/accountConsole"

export function teamConsoleErrorMessage(error: TeamConsoleError): string {
  switch (error) {
    case TeamConsoleError.NO_PERMISSION: return "You don't have access to this team."
    case TeamConsoleError.NOT_FOUND: return "Team not found."
    default: return "Internal server error."
  }
}

export function appManagementErrorMessage(error: AppManagementError): string {
  switch (error) {
    case AppManagementError.NO_PERMISSION: return "You don't have access to this team."
    case AppManagementError.NOT_FOUND: return "App not found."
    case AppManagementError.INVALID_USERNAME: return "This bot username is invalid or already taken."
    case AppManagementError.SUSPENDED_BY_OPERATOR:
      return "This bot was suspended by Argon staff, and only they can lift the suspension."
    default: return "Internal server error."
  }
}
