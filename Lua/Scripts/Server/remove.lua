local function RemoveItem(item)
    if item == nil or item.Removed then return end

	if Entity.Spawner == nil then
        Timer.Wait(function()
            RemoveItem(item)
        end, 100)
        return
    end

    Entity.Spawner.AddItemToRemoveQueue(item)
end

Hook.Add("cdremovewyx.OnDeath", "cdremovewyxOnDeath", function(effect, deltaTime, item, targets, worldPosition)
    for i2, item in ipairs(Item.ItemList) do
        if string.find(item.Tags, "cdsplinter") then
            RemoveItem(item)
        end
    end
end)
Hook.Add("cdremovewyx.OnSpawn", "cdremovewyxOnSpawn", function(effect, deltaTime, item, targets, worldPosition)
    local me = targets[1]
    for i2, item in ipairs(Item.ItemList) do
        if string.find(item.Tags, "cdsplinter") and item ~= me then
            RemoveItem(item)
        end
    end
end)
