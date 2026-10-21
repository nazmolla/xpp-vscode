# Local X++ test runtime (experimental, incomplete)

Runs the D365 F&O X++ kernel **in-process** on a developer machine to execute SysTest classes without deploying to
the cloud environment: no IIS, no AOS service, no full database sync. A scratch LocalDB database holds only the
kernel tables, the security data and the tables the tested code touches.

**Status: not working end to end yet.** The kernel starts and runs application X++ code, but table-level security
still denies access to application tables (see [Current blocker](#current-blocker)). The supported way to run tests
is `xpp_run_tests` / **Run Tests**, which run them on the environment.

## Setup

Requirements: an active D365FO configuration (unified developer environment), LocalDB (`(LocalDB)\MSSQLLocalDB`),
Visual Studio's Roslyn `csc.exe` (path in `build.ps1`), and `C:\XppLocalRuntime` on the same volume as
`PackagesLocalDirectory` (hard links). Scripts assume the root `C:\XppLocalRuntime`.

```powershell
.\setup.ps1                 # links, LocalDB database + SQL login, runtime.config, runner, schema, seed (~30 s)
.\run.ps1 -Test MyTestClass # results in C:\XppLocalRuntime\result.xml, log in runner.out
```

The test classes must be compiled into their package (`xpp_build`), and the model must reference `TestEssentials`.

## Pieces

| File | Purpose |
|---|---|
| `setup.ps1` | Packages folder of junctions (reference + custom packages); `bin` as hard links plus `StaticMetadata` and `System.ValueTuple.dll`, which a UDE bin lacks; database; SQL login (the kernel uses SQL auth); `runtime.config`. The shared install is never modified. |
| `Program.cs` → `XppLocalTestRunner.exe` | Same steps as SysTestConsole's `LocalRunner` (`Config.Init`, `ReflectionCallHelper.Start`, temp session, `XppSession.StartInPartition`, `SysTestService.runTests`) with settings from `ECA_ConfigurationFile` instead of the VS configuration. Resolves runtime dependencies from package folders, registers the AOS AppDomain, and logs infolog/kernel messages. `/diag` prints first-chance exceptions with stacks, `/trace` writes all runtime EventSource events. |
| `SchemaSync.cs` (`/schema:T1,T2 [/security]`) | Minimal schema **without starting the kernel**: database configuration, kernel tables, ID tables, then only the named tables, using SyncEngine's managed table worker in kernel-bootstrap mode. `/security` loads roles, privileges and permissions (the last step, policy tree nodes, needs the kernel and fails harmlessly). |
| `seed.sql` | Partition `initial`, company `dat`, users `Admin` and `-AOS-` (object ids, SIDs, `-SYSADMIN-` + `SYSTEMUSER` roles), flight `IfxAuditLoggingDisabled`, the `SysClientPerf` singleton, client session 2. |
| `iterate.ps1` | Run, read the SQL errors from an Extended Events capture (`xe*.sql`), sync only the missing tables, repeat. |
| `debug.ps1` | Run under `cdbX64` (WinDbg from winget, per user) and print native and managed stacks at a crash. |

## What was needed to boot the kernel (for reference)

- `Common.BinDir` is the **packages root** (module paths are `<BinDir>\<Module>\bin`), not `...\bin`.
- `Common.HostingEnvironment`, the `Monitoring.*` and `Aad.*` keys must exist; IFX audit is switched off through the
  flight `IfxAuditLoggingDisabled` in `SYSFLIGHTING`.
- The kernel needs `SysGlobalConfiguration`, `SysServerSessions`, `SysClientSessions`, `Deployment` and a few more at
  startup; `iterate.ps1` finds them from SQL errors.
- `XppSession.Start` returns no session unless `AOSAppDomainHelper.AOSAppDomain` is set, and looks users up in Entra
  through Graph unless `USERINFO.OBJECTID` is set.
- SysTestConsole cannot be reused directly: unless a global registry compatibility flag is set, it overwrites the
  database settings with the Visual Studio configuration.

## Current blocker

The kernel denies access to application tables (`Access denied to table System parameters (SystemParameters)` in a
system-startup handler; `SysClientPerf.find` returns nothing without issuing SQL, so its insert subscriber recurses
into a stack overflow). `Admin` and `-AOS-` hold `-SYSADMIN-`, and the security tables are populated, but the kernel
only reads `USERINFO`, `SECURITYROLE` and `SECURITYUSERROLE`. Next: find how the kernel decides the
system-administrator bypass for a session (assignment validity dates, role record flags, which user the temp session
runs as).
