## Comgenie.Util
Collection of useful utilities (used by both Comgenie.Server and Server.Storage).

- The EncryptedAndRepairableStream adds encryption and repair data to any stream
- CallbackStream calls custom actions for each method executed on the stream
- SubStream can create a smaller stream within a larger stream
- ForwardStream to move data between two calls which don't provide any stream themselves but only consumes them.
- SuperTree for a fast and memory efficient tag searcher, with wildcard support
- QueryTranslator turns a linq-expression into a simple parsable text filter


To get started, please take a look at the examples provided at https://github.com/Comgenie/ComgenieLibraries