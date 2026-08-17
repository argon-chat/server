# Argon.IonAccount

Ion contracts for the developer account console (`console.argon.gl`): the account surface a person
sees about their own account, their dev teams, and the applications those teams own.

Generated C# lands in `../Argon.CodeGenAccount` under the `AccountContracts` namespace — a namespace
of its own rather than the admin console's `ConsoleContracts`, so the two contract sets can grow
without colliding on a type name.

```
ionc compile -o Dotnet          # regenerate the server/client/model code
ionc compile -o Dotnet --update-lock   # ... and accept field/case renumbering
./regenerate-for-web.ps1        # regenerate the TypeScript client for the widget
```
