# Prop Firm Guardian v1.0 Manual QA Test Checklist

## Week 1 Gate Tests

- [ ] AddOn appears in NT8 Tools menu
- [ ] Window opens without crash
- [ ] Window closes without crash
- [ ] NT8 shuts down without zombie process
- [ ] Re-open AddOn after disable/enable cycle
- [ ] Memory flat in Task Manager after 1 hour

## Week 2 Gate Tests

- [ ] PnL numbers match NT8 Account tab to penny
- [ ] Breach triggers flatten in <1 second
- [ ] Post-flatten order is blocked
- [ ] 15-minute countdown displays correctly
- [ ] Tilt sequence (3 losses in 8 min) triggers lockout
- [ ] Restart NT8: PeakUnrealizedPnL restores from State.enc
- [ ] Config changes persist in Config.enc

## Week 3 Gate Tests

- [ ] Fake news event: PA flattens at T-2min, Eval ignores
- [ ] Overlapping news events: single continuous lockout
- [ ] Correlation heat flags ES+NQ concentration
- [ ] Copier: 1 slave breaches, master continues

## General Tests

- [ ] 2 sim accounts, 4 hours runtime, no crash
- [ ] 5 sim accounts, 1 hour runtime, no UI freeze
- [ ] Disconnect ethernet mid-trade: state freezes, reconnect resumes
- [ ] Audio works (or falls back to beep if no TTS voices)
