using System.Runtime.CompilerServices;

// Graph builds on Live's immutable snapshots without exposing their key index
// as a mutable public contract. This adds no dependency from Live to Graph.
[assembly: InternalsVisibleTo("BlueTusk.ContinuousGraph")]
