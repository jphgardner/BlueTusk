/** One durable stream per offline queue. Persist the stream ID and next sequence with the queued mutation. */
export function newOrderedStreamId(): string {
  const bytes = crypto.getRandomValues(new Uint8Array(8));
  return Array.from(bytes, byte => byte.toString(16).padStart(2, "0")).join("").slice(1);
}

export function orderedMutationId(streamId: string, sequence: string): string {
  if (!/^[0-9a-f]{15}$/.test(streamId) || !/^[1-9][0-9]*$/.test(sequence)) throw new TypeError("An ordered stream and positive decimal sequence are required.");
  const value = BigInt(sequence); if (value >= (1n << 60n)) throw new RangeError("The ordered sequence exceeds 60 bits.");
  const hex = value.toString(16).padStart(15, "0");
  return `${streamId.slice(0, 8)}-${streamId.slice(8, 12)}-8${streamId.slice(12)}-a${hex.slice(0, 3)}-${hex.slice(3)}`;
}

export function parseOrderedMutationId(id: string): { streamId: string; sequence: string } | null {
  const match = /^([0-9a-f]{8})-([0-9a-f]{4})-8([0-9a-f]{3})-a([0-9a-f]{3})-([0-9a-f]{12})$/i.exec(id);
  if (!match) return null;
  return { streamId: (match[1]! + match[2]! + match[3]!).toLowerCase(), sequence: BigInt(`0x${match[4]!}${match[5]!}`).toString() };
}
