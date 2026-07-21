# Compatibility and versioning

Before `1.0.0`, public APIs may change between minor releases. Patch releases should remain source- and binary-compatible unless a security or correctness defect makes that unsafe.

BlueTusk will publish an explicit PostgreSQL/.NET/EF Core support matrix before its first public preview. Protocol behavior is negotiated from server capabilities; code must not scatter version-number checks throughout feature implementations.

