package jp.co.canreplay.candecoder

/**
 * A cut-down signals.json using the real RAV4 definitions the builder emits, so
 * these tests exercise the same geometry the exhibition actually decodes.
 */
object TestSignals {
    val JSON: String = """
    {
      "format_version": 1,
      "profile": "toyota_rav4_2017",
      "vehicle": "Toyota RAV4",
      "bus": 0,
      "dbc_files": ["toyota_new_mc_pt_generated.dbc"],
      "default_cycle_time_ms": 100,
      "messages": [
        {
          "can_id": 170, "can_id_hex": "0xAA", "extended": false,
          "name": "WHEEL_SPEEDS", "dlc": 8, "comment": null,
          "cycle_time_ms_dbc": null, "observed_cycle_time_ms": 12.0,
          "observed_frame_count": 4974,
          "effective_cycle_time_ms": 12, "effective_cycle_time_source": "observed",
          "signals": [
            {"name": "WHEEL_SPEED_FR", "start_bit": 6, "length": 15,
             "byte_order": "big_endian", "signed": false, "factor": 0.01,
             "offset": -67.67, "minimum": 0.0, "maximum": 0.0, "unit": "km/h",
             "multiplexer": null, "comment": null, "values": null},
            {"name": "WHEEL_SPEED_FL", "start_bit": 22, "length": 15,
             "byte_order": "big_endian", "signed": false, "factor": 0.01,
             "offset": -67.67, "minimum": 0.0, "maximum": 0.0, "unit": "km/h",
             "multiplexer": null, "comment": null, "values": null},
            {"name": "WHEEL_SPEED_RR", "start_bit": 38, "length": 15,
             "byte_order": "big_endian", "signed": false, "factor": 0.01,
             "offset": -67.67, "minimum": 0.0, "maximum": 0.0, "unit": "km/h",
             "multiplexer": null, "comment": null, "values": null}
          ]
        },
        {
          "can_id": 37, "can_id_hex": "0x25", "extended": false,
          "name": "STEER_ANGLE_SENSOR", "dlc": 8, "comment": null,
          "cycle_time_ms_dbc": null, "observed_cycle_time_ms": 12.0,
          "observed_frame_count": 4974,
          "effective_cycle_time_ms": 12, "effective_cycle_time_source": "observed",
          "signals": [
            {"name": "STEER_ANGLE", "start_bit": 3, "length": 12,
             "byte_order": "big_endian", "signed": true, "factor": 1.5,
             "offset": 0.0, "minimum": -500.0, "maximum": 500.0, "unit": "deg",
             "multiplexer": null, "comment": null, "values": null},
            {"name": "STEER_FRACTION", "start_bit": 39, "length": 4,
             "byte_order": "big_endian", "signed": true, "factor": 0.1,
             "offset": 0.0, "minimum": -0.7, "maximum": 0.7, "unit": "deg",
             "multiplexer": null, "comment": null, "values": null}
          ]
        },
        {
          "can_id": 956, "can_id_hex": "0x3BC", "extended": false,
          "name": "GEAR_PACKET", "dlc": 8, "comment": null,
          "cycle_time_ms_dbc": null, "observed_cycle_time_ms": 100.0,
          "observed_frame_count": 600,
          "effective_cycle_time_ms": 100, "effective_cycle_time_source": "observed",
          "signals": [
            {"name": "GEAR", "start_bit": 13, "length": 6,
             "byte_order": "big_endian", "signed": false, "factor": 1.0,
             "offset": 0.0, "minimum": 0.0, "maximum": 0.0, "unit": "",
             "multiplexer": null, "comment": null,
             "values": {"0": "D", "1": "S", "8": "N", "16": "R", "32": "P"}}
          ]
        }
      ],
      "undecoded_can_ids": [
        {"can_id": 1017, "can_id_hex": "0x3F9",
         "observed_frame_count": 600, "observed_cycle_time_ms": 100.0}
      ],
      "value_tables": {}
    }
    """.trimIndent()
}
