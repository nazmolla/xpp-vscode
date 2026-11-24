# @fno-studio/core

F&O Studio's cloud operations for Dynamics 365 Finance & Operations unified developer environments (UDE), without
VS Code. The F&O Studio extension and the Claude Code plugin both use it. Windows, Node 20+.

- Dataverse client and sign-in through a `TokenProvider` you supply
- Deploy built packages and synchronize the database (`deployPackages`, `syncDatabase`)
- Run SysTest classes on the cloud runtime (`runTests`)
- Just-in-time SQL credentials (`requestSqlCredentials`, Reader by default) and read-only queries through
  F&O Studio's data service (`DataClient`, `readOnlyQuery`)
- XPPConfig and registry discovery (`loadEnvironment`, `listEnvironments`), the connection check and the full UDE
  setup (`checkUdeConnection`, `runUdeSetup`), deployed-versus-built staleness

The host supplies sign-in (`TokenProvider.getToken(resource, { interactive })`, scope `<resource>/.default`), output
(`Log.line`, `Progress.report`), cancellation (`AbortSignal`) and settings (plain option objects). The core never
shows UI or reads settings.

## Build and use

```
npm run build        # tsc: dist/*.js and dist/*.d.ts
npm test             # node --test: fake TokenProvider, recorded HTTP responses (test/fixtures)
npm pack             # fno-studio-core-<version>.tgz, for a consumer outside this repository
```

```js
const { Dataverse, builtPackages, deployPackages } = require('@fno-studio/core');
const dv = await Dataverse.connect('https://yourorg.crm.dynamics.com', tokens);
await deployPackages(dv, await builtPackages(modelStoreFolder), 'Module', { log: { line: console.log } });
```

Database queries go through F&O Studio's server (`XppVsCode.Server.exe`) started with `--allow-data` by the host
itself; never give `--allow-data` to the server used as an MCP server.
