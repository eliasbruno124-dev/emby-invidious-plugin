import shutil
f = '/docker/portainer/data/compose/20/docker-compose.yml'
shutil.copy2(f, f + '.bak')
with open(f) as fh:
    c = fh.read()

changes = 0

# 1. Invidious: comment out network_mode, uncomment ports
old = '    container_name: invidious\n    network_mode: "service:gluetun"\n    #ports:\n     # - "3034:3000"'
new = '    container_name: invidious\n    #network_mode: "service:gluetun"\n    ports:\n      - "3034:3000"'
if old in c:
    c = c.replace(old, new)
    changes += 1
    print('[OK] Invidious: network_mode commented, ports uncommented')
else:
    print('[SKIP] Invidious network_mode/ports not found')

# 2. Companion URL: gluetun -> companion
old = 'private_url: "http://gluetun:8282/companion"'
new = 'private_url: "http://companion:8282/companion"'
if old in c:
    c = c.replace(old, new)
    changes += 1
    print('[OK] Companion URL updated')
else:
    print('[SKIP] Companion URL not found')

# 3. Invidious depends_on: remove gluetun
old = '      - invidious-db\n      - gluetun\n\n\n  companion:'
new = '      - invidious-db\n\n\n  companion:'
if old in c:
    c = c.replace(old, new)
    changes += 1
    print('[OK] Invidious depends_on: gluetun removed')
else:
    print('[SKIP] Invidious depends_on gluetun not found')

# 4. Companion: remove network_mode and depends_on gluetun
old = '       - SERVER_SECRET_KEY=eiYe1Eith5Maw3jo\n    network_mode: "service:gluetun"\n    depends_on:\n      - gluetun\n    restart:'
new = '       - SERVER_SECRET_KEY=eiYe1Eith5Maw3jo\n    restart:'
if old in c:
    c = c.replace(old, new)
    changes += 1
    print('[OK] Companion: network_mode + depends_on removed')
else:
    print('[SKIP] Companion network_mode/depends not found')

# 5. Companion: uncomment ports
old = '    #ports:\n     #- "8282:8282"'
new = '    ports:\n      - "8282:8282"'
if old in c:
    c = c.replace(old, new)
    changes += 1
    print('[OK] Companion: ports uncommented')
else:
    print('[SKIP] Companion ports not found')

# 6. Gluetun: remove invidious/companion ports
old = '      - "8282:8282"\n      - "3034:3000"\n'
if old in c:
    c = c.replace(old, '')
    changes += 1
    print('[OK] Gluetun: removed ports 8282 and 3034')
else:
    print('[SKIP] Gluetun ports 8282/3034 not found')

with open(f, 'w') as fh:
    fh.write(c)
print(f'\nTotal changes: {changes}/6')
if changes == 6:
    print('All changes applied successfully!')
else:
    print('WARNING: Some changes were not applied. Check the output above.')
