import("lib.detect.find_tool")
local p = os.getenv("PATH") or ""
local hit = false
for _, entry in ipairs(path.splitenv(p)) do
    if entry:find("cf_v1") then
        print("PATH entry:", entry, "isfile=", os.isfile(entry))
        hit = true
    end
end
if not hit then print("no cf_v1 entry in xmake PATH") end
local n = find_tool("ninja", {force = true})
print("ninja =>", n and n.program or "NIL")
