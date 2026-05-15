#!/usr/bin/env python3
"""
LSL Test Sender for Virtual Hand Interface

This script sends test data to the Godot hand visualization via LSL.
It simulates EMG predictions with smooth sinusoidal movements.

Data format (9 channels):
- 0: Thumb flexion (0-1)
- 1: Thumb abduction (0-1)
- 2: Index flexion (0-1)
- 3: Middle flexion (0-1)
- 4: Ring flexion (0-1)
- 5: Pinky flexion (0-1)
- 6: Wrist flexion (0-1, currently unused)
- 7: Wrist abduction (0-1, currently unused)
- 8: Wrist rotation (0-1, currently unused)
"""

import time
import numpy as np

try:
    from pylsl import StreamInfo, StreamOutlet
except ImportError:
    print("Error: pylsl not installed. Install with: pip install pylsl")
    exit(1)

FREQUENCY = 32


def create_outlet():
    """Create an LSL outlet for hand predictions."""
    info = StreamInfo(
        name="MyoGestic_Output",
        type="MyoGestic_9DVector",
        channel_count=9,
        nominal_srate=FREQUENCY,
        channel_format="float32", # type: ignore
        source_id="test_emg_001",
    )

    # Add channel labels
    channels = info.desc().append_child("channels")
    labels = [
        "ThumbFlexion",
        "ThumbAbduction",
        "IndexFlexion",
        "MiddleFlexion",
        "RingFlexion",
        "PinkyFlexion",
        "WristFlexion",
        "WristAbduction",
        "WristRotation",
    ]

    for label in labels:
        channels.append_child("channel").append_child_value("label", label)

    outlet = StreamOutlet(info)
    print(f"✅ Created LSL outlet: {info.name()} ({info.type()})")
    print(f"   Channels: {info.channel_count()}")
    print(f"   Sampling rate: {info.nominal_srate()} Hz")
    return outlet


def generate_test_pattern(t, pattern="wave"):
    """Generate test data patterns.

    Args:
        t: Time in seconds
        pattern: Pattern type ('wave', 'fist', 'pinch', 'individual', 'random')

    Returns:
        9-element array of float values (0-1)
    """
    data = np.zeros(9, dtype=np.float32)

    if pattern == "wave":
        # Smooth wave pattern through all fingers
        phase = 2 * np.pi * t * 0.2  # 0.2 Hz
        data[0] = (np.sin(phase) + 1) / 2  # Thumb flexion
        data[1] = 0.3  # Thumb abduction
        data[2] = (np.sin(phase + np.pi / 4) + 1) / 2  # Index
        data[3] = (np.sin(phase + np.pi / 2) + 1) / 2  # Middle
        data[4] = (np.sin(phase + 3 * np.pi / 4) + 1) / 2  # Ring
        data[5] = (np.sin(phase + np.pi) + 1) / 2  # Pinky

    elif pattern == "fist":
        # Close and open fist
        phase = 2 * np.pi * t * 0.3
        value = (np.sin(phase) + 1) / 2
        data[0] = value * 0.67  # Thumb (partial)
        data[1] = value  # Thumb abduction
        data[2:6] = value  # All fingers

    elif pattern == "pinch":
        # Pinch gesture (thumb + index)
        phase = 2 * np.pi * t * 0.3
        value = (np.sin(phase) + 1) / 2
        data[0] = value * 0.5  # Thumb
        data[1] = value  # Thumb abduction
        data[2] = value * 0.6  # Index

    elif pattern == "individual":
        # Move fingers individually in sequence
        finger_idx = int((t * 0.5) % 5) + 2  # Cycle through fingers
        phase = 2 * np.pi * ((t * 2) % 1)
        value = (np.sin(phase) + 1) / 2
        if finger_idx == 2:  # Special thumb handling
            data[0] = value
            data[1] = 0.3
        else:
            data[finger_idx] = value

    elif pattern == "random":
        # Random smooth movements
        for i in range(6):
            phase = 2 * np.pi * t * (0.1 + i * 0.05)
            data[i] = (np.sin(phase + i) + 1) / 2

    return -data


def main():
    """Main loop to send LSL data."""
    print("\n" + "=" * 50)
    print("LSL Hand Prediction Test Sender")
    print("=" * 50)

    # Create outlet
    outlet = create_outlet()

    # Available patterns
    patterns = ["wave", "fist", "pinch", "individual", "random"]
    current_pattern_idx = 0
    pattern_duration = 10.0  # seconds per pattern

    print(f"\nSending data at {FREQUENCY} Hz...")
    print("Pattern will change every 10 seconds")
    print("Press Ctrl+C to stop\n")

    start_time = time.time()
    sample_count = 0

    try:
        while True:
            current_time = time.time() - start_time

            # Change pattern every 10 seconds
            pattern_idx = int(current_time / pattern_duration) % len(patterns)
            if pattern_idx != current_pattern_idx:
                current_pattern_idx = pattern_idx
                print(f"\n🔄 Switching to pattern: {patterns[current_pattern_idx]}")

            # Generate and send sample
            sample = generate_test_pattern(current_time, patterns[current_pattern_idx])
            outlet.push_sample(sample.tolist())

            sample_count += 1

            # Print status every second
            if sample_count % FREQUENCY == 0:
                print(
                    f"⏱️  Time: {current_time:.1f}s | Pattern: {patterns[current_pattern_idx]} | Samples: {sample_count}"
                )

            # Sleep to maintain FREQUENCY Hz
            time.sleep(1.0 / FREQUENCY)

    except KeyboardInterrupt:
        print("\n\n✅ Stopped sending data")
        print(f"Total samples sent: {sample_count}")
        print(f"Total time: {time.time() - start_time:.1f}s")


if __name__ == "__main__":
    main()
