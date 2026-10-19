type Cell = string | number | boolean | null;

const show = (cell: Cell): string => {
  if (cell === null) {
    return '—';
  }

  if (typeof cell === 'number') {
    return String(Math.round(cell * 10000) / 10000);
  }

  return String(cell);
};

const isNumericColumn = (rows: Cell[][], column: number): boolean =>
  rows.some(row => typeof row[column] === 'number') &&
  rows.every(row => row[column] === null || typeof row[column] === 'number');

const DataTable = ({ columns, rows }: { columns: string[]; rows: Cell[][] }) => (
  <div className="data-table-wrap">
    <table className="data-table">
      <thead>
        <tr>
          {columns.map((column, index) => (
            <th key={column} className={isNumericColumn(rows, index) ? 'numeric' : undefined}>{column}</th>
          ))}
        </tr>
      </thead>
      <tbody>
        {rows.map((row, index) => (
          <tr key={index}>
            {row.map((cell, column) => (
              <td key={column} className={typeof cell === 'number' ? 'numeric' : undefined}>{show(cell)}</td>
            ))}
          </tr>
        ))}
      </tbody>
    </table>
  </div>
);

export default DataTable;
