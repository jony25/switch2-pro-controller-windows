# DSU (Cemuhook) motion output

The app can act as a Cemuhook "DSU" server, so emulators that read motion over UDP get the controller's gyroscope and accelerometer. Implemented in `src/Switch2ProManager/Services/CemuhookServer.cs`.

Output only: nothing is sent to the controller.

## Enabling it

- Tick **DSU motion server for emulators (127.0.0.1:26760)** in the General tab.
- The server listens on `127.0.0.1:26760` only. Other machines on the network cannot read it.
- Turning it on also turns on motion reading while connected (the same as the Motion checkbox in the test tab).
- If the port is busy, the log shows `DSU server not started` and the box is unticked again.
- The server sends motion data only while the controller is connected. Eden's connection test needs that data, so connect the controller first; without it the test reports "Could not receive valid data".
- The log (`%LOCALAPPDATA%\Switch2ProManager\logs\app.log`) shows the requests from emulators (`DSU info request`, `DSU client subscribed`) and a summary every 5 s while clients are connected (`DSU status`).

## Emulator setup: Eden and Eglantine

Eden is the reference client here. Eglantine is a fork of Eden and is assumed to share its motion code; it has not been tested.

1. Connect the controller and tick the DSU box in the app.
2. In Eden, open the motion settings for the player (**Emulation → Configure → Controls**, then the motion or motion/touch section) and choose the **CemuhookUDP** device.
3. Server `127.0.0.1`, port `26760`, pad index `0`. These are Eden's defaults.
4. Start a game that uses motion and tilt the controller.

## Protocol

Protocol reference: community documentation, [v1993/cemuhook-protocol](https://v1993.github.io/cemuhook-protocol/). Constants: protocol version 1001, magic `DSUS` (server) and `DSUC` (client), CRC-32 over the whole packet with the CRC field set to zero.

Message types served:

| Type | Request | Reply |
|---|---|---|
| `0x100000` | protocol version | 22 bytes |
| `0x100001` | controller info | 32 bytes (slot 0, connected or not) |
| `0x100002` | subscribe to controller data | 100-byte data packets while connected, about 125 per second, to every subscriber |

A subscriber expires after 5 seconds without a new `0x100002` request. Only slot 0 is served, which is Eden's default pad index. Battery is not reported (0).

## Mapping

| DSU field | Source | Status |
|---|---|---|
| Stick X/Y | left and right stick, centred at 128, up positive | Community spec; up-positive convention matches the app's own stick values |
| D-pad, +, −, L3, R3, L, R, ZL, ZR, Home | bits as in the community spec | Eden reads the same bits (its source comments name them Share, L3, R3, Options, Up, Right, Down, Left, L2, R2, L1, R1) |
| Face buttons | **by physical position with the Nintendo letters**: bit7 = Y (left), bit6 = B (bottom), bit5 = A (right), bit4 = X (top) | Matches Eden's reading of the bits (Square = left, Cross = bottom, Circle = right, Triangle = top); **corrected** after reading Eden's source |
| Analog buttons | 255 when pressed, 0 otherwise, order Y, B, A, X, R1, L1, R2, L2 | Community spec. Eden's struct gives a different order from offset 28 onward; see *Open points* |
| Accelerometer | `AccelX/Y/Z` in g | Units from the spec; axis direction **not verified** |
| Gyroscope | `GyroX` → pitch, `GyroY` → yaw, `GyroZ` → roll, in deg/s | **Not verified**: which controller axis is pitch, yaw or roll, and the signs |
| Touch | zero | Not available on this controller |

## Verified

- Packet sizes (22, 32 and 100 bytes), header, CRC-32 and field offsets: checked by a test client that talks to the server over UDP (version, info, subscription, data, disconnected controller). The CRC-32 of `123456789` matches the reference value `CBF43926`.
- Button bits, stick scaling, analog button values and motion values in the packet: checked against values written in the test.
- Face-button bit meaning: checked against Eden's source comments, not against Eden running.

## Open points (need Eden and the controller)

1. ~~**Eden shows the motion**~~ **Checked:** with the controller connected, Eden receives the packets and the motion works.
2. **Gyro axes and signs:** tilt the controller and compare with the emulator. The PadForge project (another DSU server) negates the accelerometer X, Y, Z and the gyro yaw and roll before sending, against the sensor frame it reads. Our frame is not known to match theirs, so this is a hint to test, not a fix.
3. **Analog button order:** the community spec puts Y, B, A, X at offsets 28–31 but Eden's struct puts R1 there. The digital bits agree between the two sources, so the digital buttons are the reliable path. If an analog-only feature in Eden misbehaves, this is the first place to look.
4. **Packet rate:** the server sends at most about 125 packets per second.
