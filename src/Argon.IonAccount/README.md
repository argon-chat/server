# Argon.IonAccount

Ion contracts for the developer account console (`console.argon.gl`): the account surface a person
sees about their own account, their dev teams, and the applications those teams own.

The C# is generated when `../Argon.CodeGenAccount` builds (the `ionpath.compiler` MSBuild SDK writes it
to that project's `obj/`, never into the repository), under the `AccountContracts` namespace — a
namespace of its own rather than the admin console's `ConsoleContracts`, so the two contract sets can
grow without colliding on a type name.

```
dotnet build ../Argon.CodeGenAccount                        # regenerate the server/client/model code
dotnet build ../Argon.CodeGenAccount -p:IonLockMode=update  # ... and record the schema in ion.lock.json
ionc lock update                # accept field/case renumbering
./regenerate-for-web.ps1        # regenerate the TypeScript client for the widget
```
