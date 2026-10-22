using System.Collections.Generic;

namespace CardShopCoop.Sync
{
    internal static class PlayTableSaveRepair
    {
        public static bool UpdatePrefix(ShelfManager __instance, InteractablePlayTable playTable)
        {
            if (CoopCore.Role != CoopRole.Host)
                return true;
            // Do not replace the save while LoadInteractableObjectData is still
            // constructing the live registry from it.
            if (!__instance.m_FinishLoadingObjectData)
                return false;
            if (playTable == null || !__instance.m_PlayTableList.Contains(playTable))
                return false;

            int oldCount = CPlayerData.m_PlayTableSaveDataList?.Count ?? 0;
            var snapshot = LiveSaveSnapshot.Capture(__instance.m_PlayTableList,
                table => table != null, CaptureTable);
            // Publish only the table data after every record is complete. A full
            // furniture save here would re-enter unrelated customer/worker saves.
            __instance.m_PlayTableSaveDataList = snapshot;
            CPlayerData.m_PlayTableSaveDataList = snapshot;
            if (oldCount != snapshot.Count)
                CoopPlugin.Log.LogInfo($"Rebuilt play-table save records: {oldCount} -> {snapshot.Count}");
            return false;
        }

        private static PlayTableSaveData CaptureTable(InteractablePlayTable table)
        {
            var data = new PlayTableSaveData();
            data.pos.SetData(table.transform.position);
            data.rot.SetData(table.transform.rotation);
            data.objectType = table.m_ObjectType;
            data.isBoxed = table.GetIsBoxedUp();
            if (data.isBoxed)
            {
                var box = table.GetPackagingBoxShelf();
                // Preserve recoverable placement if a delivery is between attaching
                // its table and its package wrapper this frame.
                var location = box != null ? box.transform : table.transform;
                data.boxedPackagePos.SetData(location.position);
                data.boxedPackageRot.SetData(location.rotation);
            }
            if (table.GetIsMovingObject())
            {
                data.isBoxed = true;
                data.boxedPackagePos.SetData(table.transform.position);
                data.boxedPackageRot.SetData(table.transform.rotation);
            }
            data.hasStartPlay = table.GetHasStartPlay();
            data.isTournamentPlayTable = table.GetIsTournamentPlayTable();
            data.tournamentTableNumber = table.GetTournamentPlayTableNumber();
            data.isSeatOccupied = Copy(table.GetIsSeatOccupied());
            data.isPlayerSeat = Copy(table.GetIsPlayerSeat());
            data.isCustomerSmelly = Copy(table.GetIsCustomerSmelly());
            data.customerTournamentDataList = Copy(table.GetCustomerTournamentDataList());
            data.tableGameItemSetDataList = Copy(table.GetTableGameItemSetDataList());
            data.playTableFee = Copy(table.GetPlayTableFee());
            data.currentPlayerCount = table.GetCurrentPlayerCount();
            data.currentPlayTime = table.GetCurrentPlayTime();
            data.currentPlayTimeMax = table.GetCurrentPlayTimeMax();
            return data;
        }

        private static List<T> Copy<T>(List<T> values) => values == null ? null : new List<T>(values);
    }
}
