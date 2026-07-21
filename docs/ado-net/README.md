# ADO.NET

Version 0.0.2 provides an initial asynchronous `BlueTuskConnection`, `BlueTuskCommand`, buffered `BlueTuskDataReader`, provider factory, and unpooled `BlueTuskDataSource`. Execution currently uses PostgreSQL's simple-query protocol, so parameters deliberately fail instead of interpolating values into SQL. Extended queries and typed parameters are the next milestone.
