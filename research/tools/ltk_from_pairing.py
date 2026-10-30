#!/usr/bin/env python3
"""Derive the BLE link key of a Switch 2 controller from a captured pairing.

The Switch 2 does application-level pairing (command 0x15), not BLE SMP. Per community documentation
(ndeadly/switch2_controller_research):
  * the host sends its 16-byte key A1 in subcommand 0x04 ("exchange keys"):   15 91 01 04 00 11 00 00 | 00 <A1 x16>
  * the controller answers with B1, which is a constant: 5CF6EE792CDF05E1BA2B6325C41A5F10
  * LTK = A1 XOR B1

Usage:  python ltk_from_pairing.py <A1 as 32 hex chars>
Wireshark expects the LTK in a fixed byte order that we have not verified for this device, so both are printed.
"""
import sys

B1 = bytes.fromhex("5cf6ee792cdf05e1ba2b6325c41a5f10")


def main():
    if len(sys.argv) != 2:
        sys.exit(__doc__)
    a1 = bytes.fromhex(sys.argv[1].replace(" ", ""))
    if len(a1) != 16:
        sys.exit("A1 must be exactly 16 bytes (32 hex characters)")
    ltk = bytes(x ^ y for x, y in zip(a1, B1))
    print("LTK (as computed)  :", ltk.hex())
    print("LTK (byte-reversed):", ltk[::-1].hex())
    print("Try the byte-reversed value first in Wireshark; if the link still does not decrypt, try the other one.")


if __name__ == "__main__":
    main()
