local actor, failure = authenticate(false)
if failure then return result(failure) end
if not points or points < 1 or points > 100 or points ~= math.floor(points) then return result('invalid_input') end
checkType(board, 'zset')
local rows = redis.call('ZREVRANGE', board, 0, points - 1, 'WITHSCORES')
local entries = {}
for i = 1, #rows, 2 do entries[#entries + 1] = {playerId=rows[i], score=tonumber(rows[i + 1])} end
local body = #entries == 0 and '[]' or cjson.encode(entries)
touch(actor)
return result('ok', body)
