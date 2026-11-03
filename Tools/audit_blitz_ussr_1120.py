#!/usr/bin/env python3
"""Run independent source-SCG geometry/UV/normal/plate/pixel gates on Object 907."""
from unittest.mock import patch
import port_blitz_ussr_1120 as ussr
import audit_blitz_1120 as audit


def main():
    ussr.configure_importer(list(ussr.VEHICLES))
    destination = ussr.DATA / 'blitz_ussr_1120_geometry_audit.json'
    original_save = audit.p.save

    def save(path, value):
        original_save(destination if path.name == 'blitz_1120_geometry_audit.json' else path, value)

    with patch.object(audit.p, 'CARD_FILE', ussr.CARDS), patch.object(audit.p, 'save', save):
        return audit.run()


if __name__ == '__main__':
    raise SystemExit(main())
