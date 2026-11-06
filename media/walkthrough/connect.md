# Connect to your environment

**Unified developer environment (UDE):** F&O Studio does what Visual Studio's *Connect to Dataverse* does:

1. Signs in to Dataverse and reads the environment's application version.
2. Downloads the standard metadata (PackagesLocalDirectory) and the cross-reference database. Needs about 16 GB free.
3. Restores the cross-reference database into SQL Server LocalDB.
4. Writes the configuration Visual Studio also uses, so both tools share it.

Each step shows its progress; a failed step can be retried without starting over.

**Cloud-hosted or VHD environment:** the metadata is already on the machine. Nothing to do.

Several environments can be connected; **Select Environment** (or the environment in the status bar) switches between them per workspace.
