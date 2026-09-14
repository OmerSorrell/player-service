-- Warmup declares no keys and must return before accessing any application data.
if ARGV[11] == 'warmup' then return {'ok', '', 0} end
local token, playerId, otherId = ARGV[1], ARGV[2], ARGV[3]
local points, requestId = tonumber(ARGV[4]), ARGV[5]
local lease, retention = tonumber(ARGV[6]), tonumber(ARGV[7])
local resolvedPlayerId, resolvedDeviceId, previousToken = ARGV[8], ARGV[9], ARGV[10]
local clock = redis.call('TIME')
local now = tonumber(clock[1]) * 1000 + math.floor(tonumber(clock[2]) / 1000)
local maximum = 2147483647
local sessionKey, deviceKey, playerKey, playerSessions, board = KEYS[1], KEYS[2], KEYS[3], KEYS[4], KEYS[5]

local function checkType(key, expected)
    local actual = redis.call('TYPE', key).ok
    if actual ~= 'none' and actual ~= expected then error('Invalid key type: expected ' .. expected) end
end

local function validId(id)
    return #id > 0 and #id <= 64 and string.match(id, '^[%w_-]+$') ~= nil
end

local function result(code, body, replay)
    return {code, body or '', replay or 0}
end

local function readPlayer(key, id)
    checkType(key, 'hash')
    if redis.call('EXISTS', key) == 0 then return nil end
    local fields = redis.call('HMGET', key, 'score', 'giftsSent', 'giftsReceived', 'lastActive')
    local values = {}
    for i = 1, 4 do
        values[i] = tonumber(fields[i])
        if not values[i] or values[i] < 0 or values[i] ~= math.floor(values[i]) then error('Invalid player schema') end
        if i < 4 and values[i] > maximum then error('Invalid player numeric range') end
    end
    return {playerId=id, score=values[1], giftsSent=values[2], giftsReceived=values[3], lastActive=values[4]}
end

local function authenticate(requireOwner)
    checkType(sessionKey, 'hash')
    if redis.call('PTTL', sessionKey) <= 0 then return nil, 'unauthorized' end
    local fields = redis.call('HMGET', sessionKey, 'playerId', 'deviceId')
    if not fields[1] or not fields[2] then return nil, 'unauthorized' end
    -- The preliminary C# lookup is only a hint for declaring keys. Never use a stale identity.
    if fields[1] ~= resolvedPlayerId or fields[2] ~= resolvedDeviceId then return nil, 'unauthorized' end
    checkType(deviceKey, 'string')
    if redis.call('GET', deviceKey) ~= token or redis.call('PTTL', deviceKey) <= 0 then return nil, 'unauthorized' end
    if requireOwner and fields[1] ~= playerId then return nil, 'forbidden' end
    checkType(playerSessions, 'zset')
    local actor = readPlayer(playerKey, fields[1])
    if not actor then error('Session has no player') end
    return {playerId=fields[1], player=actor}, nil
end

local function touch(actor)
    local active = playerSessions
    -- Bound cleanup work independently of the number of devices a player has used.
    local expired = redis.call('ZRANGEBYSCORE', active, '-inf', now, 'LIMIT', 0, 32)
    if #expired > 0 then redis.call('ZREM', active, unpack(expired)) end
    redis.call('PEXPIRE', sessionKey, lease * 1000)
    redis.call('PEXPIRE', deviceKey, lease * 1000)
    redis.call('ZADD', active, now + lease * 1000, token)
    redis.call('PEXPIRE', active, (lease + 60) * 1000)
    redis.call('HSET', playerKey, 'lastActive', now)
end

local function cached(key, fingerprint, actor)
    checkType(key, 'string')
    local value = redis.call('GET', key)
    if not value then return nil end
    local saved = cjson.decode(value)
    if saved.fingerprint ~= fingerprint then return result('idempotency_conflict') end
    if saved.code == 'ok' then touch(actor) end
    return result(saved.code, saved.body, 1)
end

local function save(key, fingerprint, code, body)
    redis.call('SET', key, cjson.encode({fingerprint=fingerprint, code=code, body=body or ''}), 'EX', retention)
    return result(code, body)
end

local function writeScore(key, id, score)
    redis.call('HSET', key, 'score', score)
    redis.call('ZADD', board, score, id)
end
