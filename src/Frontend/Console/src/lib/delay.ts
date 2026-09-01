export default function (time: number): Promise<void> {
  return new Promise((res) => {
    setTimeout(res, time);
  });
}
